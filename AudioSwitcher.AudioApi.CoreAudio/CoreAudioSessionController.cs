using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AudioSwitcher.AudioApi.CoreAudio.Interfaces;
using AudioSwitcher.AudioApi.CoreAudio.Threading;
using AudioSwitcher.AudioApi.Observables;
using AudioSwitcher.AudioApi.Session;
using System.Collections.ObjectModel;

namespace AudioSwitcher.AudioApi.CoreAudio
{
    public sealed class CoreAudioSessionController : IAudioSessionController, IAudioSessionNotification, IDisposable, IEnumerable<IAudioSession>
    {
        private readonly IAudioSessionManager2 _audioSessionManager;

        private readonly CoreAudioDevice _device;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private readonly IDisposable _processTerminatedSubscription;

        private readonly Broadcaster<IAudioSession> _sessionCreated;
        private readonly Broadcaster<string> _sessionDisconnected;

        private Dictionary<string, CoreAudioSession> _sessionCache;

        internal CoreAudioSessionController(CoreAudioDevice device, IAudioSessionManager2 audioSessionManager)
        {
            if (audioSessionManager == null)
                throw new ArgumentNullException(nameof(audioSessionManager));

            ComThread.Assert();

            _device = device;
            _audioSessionManager = audioSessionManager;
            _audioSessionManager.RegisterSessionNotification(this);
            //_sessionCache = new List<CoreAudioSession>();

            _sessionCreated = new Broadcaster<IAudioSession>();
            _sessionDisconnected = new Broadcaster<string>();

            RefreshSessions();

            _processTerminatedSubscription = ProcessMonitor.ProcessTerminated.Subscribe(processId =>
            {
                RemoveSessions(_sessionCache.Values.Where(x => x.ProcessId == processId));
            });

        }

        public IObservable<IAudioSession> SessionCreated => _sessionCreated.AsObservable();

        public IObservable<string> SessionDisconnected => _sessionDisconnected.AsObservable();

        public IEnumerable<IAudioSession> All()
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _sessionCache.Values;
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        public Task<IEnumerable<IAudioSession>> AllAsync()
        {
            return TaskShim.FromResult(All());
        }

        public IEnumerable<IAudioSession> ActiveSessions()
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _sessionCache.Values.Where(x => x.SessionState == AudioSessionState.Active).ToList();
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        public Task<IEnumerable<IAudioSession>> ActiveSessionsAsync()
        {
            return TaskShim.FromResult(ActiveSessions());
        }

        public IEnumerable<IAudioSession> InactiveSessions()
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _sessionCache.Values.Where(x => x.SessionState == AudioSessionState.Inactive).ToList();
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        public Task<IEnumerable<IAudioSession>> InactiveSessionsAsync()
        {
            return TaskShim.FromResult(InactiveSessions());
        }

        public IEnumerable<IAudioSession> ExpiredSessions()
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _sessionCache.Values.Where(x => x.SessionState == AudioSessionState.Expired).ToList();
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        public Task<IEnumerable<IAudioSession>> ExpiredSessionsAsync()
        {
            return TaskShim.FromResult(ExpiredSessions());
        }

        public IEnumerator<IAudioSession> GetEnumerator()
        {
            return All().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        int IAudioSessionNotification.OnSessionCreated(IAudioSessionControl sessionControl)
        {
            TaskShim.Run(async () => await CreateSession(sessionControl));

            return 0;
        }

        private async Task CreateSession(IAudioSessionControl sessionControl)
        {
            var managedSession = await ComThread.BeginInvoke(() => CacheSessionWrapper(sessionControl)).ConfigureAwait(false);

            if (managedSession != null)
                OnSessionCreated(managedSession);
        }

        public void Dispose()
        {
            _processTerminatedSubscription.Dispose();

            _sessionCreated.Dispose();
            _sessionDisconnected.Dispose();
            _lock.Dispose();

            Marshal.FinalReleaseComObject(_audioSessionManager);
        }

        private void RefreshSessions()
        {
            IAudioSessionEnumerator enumerator;
            _audioSessionManager.GetSessionEnumerator(out enumerator);

            if (enumerator == null)
                return;

            int count;
            enumerator.GetCount(out count);

            _sessionCache = new Dictionary<string, CoreAudioSession>(count);

            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl session;
                enumerator.GetSession(i, out session);
                var managedSession = CacheSessionWrapper(session);
                OnSessionCreated(managedSession);
            }
        }

        private CoreAudioSession CacheSessionWrapper(IAudioSessionControl session)
        {
            var managedSession = new CoreAudioSession(_device, session);

            //There's some dumb crap in the Api that causes the sessions to still appear
            //even after the process has been terminated
            if (Process.GetProcesses().All(x => x.Id != managedSession.ProcessId))
                return null;

            var acquiredLock = _lock.AcquireReadLockNonReEntrant();
            try
            {
                bool existing = _sessionCache.TryGetValue(managedSession.InstanceId, out var existingSession);
                if (existing)
                {
                    managedSession.Dispose();
                    return existingSession;
                }
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }

            managedSession.StateChanged.Subscribe(ManagedSessionOnStateChanged);
            managedSession.Disconnected.Subscribe(ManagedSessionOnDisconnected);

            acquiredLock = _lock.AcquireWriteLockNonReEntrant();

            try
            {
                _sessionCache.Add(managedSession.InstanceId, managedSession);
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitWriteLock();
            }

            return managedSession;
        }

        private void ManagedSessionOnStateChanged(SessionStateChangedArgs changedArgs)
        {
        }

        private void ManagedSessionOnDisconnected(SessionDisconnectedArgs disconnectedArgs)
        {
            bool found = _sessionCache.TryGetValue(disconnectedArgs.Session.Id, out var session);

            if (found) RemoveSessions(new CoreAudioSession[] { session });
        }

        private void RemoveSessions(IEnumerable<CoreAudioSession> sessions)
        {
            var coreAudioSessions = sessions as IList<CoreAudioSession> ?? sessions.ToList();

            if (!coreAudioSessions.Any())
                return;

            var acquiredLock = _lock.AcquireWriteLockNonReEntrant();

            try
            {
                foreach (CoreAudioSession session in sessions)
                {
                    _sessionCache.Remove(session.InstanceId);
                }
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitWriteLock();
            }

            foreach (var s in coreAudioSessions)
            {
                OnSessionDisconnected(s);
                s.Dispose();
            }
        }

        private void OnSessionCreated(IAudioSession session)
        {
            _sessionCreated.OnNext(session);
        }

        private void OnSessionDisconnected(IAudioSession session)
        {
            _sessionDisconnected.OnNext(session.Id);
        }
    }
}