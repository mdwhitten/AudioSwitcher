using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AudioSwitcher.AudioApi.CoreAudio.Interfaces;
using AudioSwitcher.AudioApi.CoreAudio.Threading;
using AudioSwitcher.AudioApi.Observables;

namespace AudioSwitcher.AudioApi.CoreAudio
{
    /// <summary>
    ///     Enumerates Windows System Devices.
    ///     Stores the current devices in memory to avoid calling the COM library when not required
    /// </summary>
    public sealed class CoreAudioController : AudioController<CoreAudioDevice>
    {
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
        private Dictionary<string, CoreAudioDevice> _deviceCache = new Dictionary<string, CoreAudioDevice>();
        private volatile IntPtr _innerEnumeratorPtr;
        private readonly ThreadLocal<IMultimediaDeviceEnumerator> _innerEnumerator;
        private SystemEventNotifcationClient _systemEvents;

        private IMultimediaDeviceEnumerator InnerEnumerator => _innerEnumerator.Value;

        public CoreAudioController(EDeviceState deviceState = EDeviceState.Active)
        {
            // ReSharper disable once SuspiciousTypeConversion.Global
            var innerEnumerator = ComObjectFactory.GetDeviceEnumerator();
            _innerEnumeratorPtr = Marshal.GetIUnknownForObject(innerEnumerator);

            if (innerEnumerator == null)
                throw new InvalidComObjectException("No Device Enumerator");

            _innerEnumerator = new ThreadLocal<IMultimediaDeviceEnumerator>(() => Marshal.GetUniqueObjectForIUnknown(_innerEnumeratorPtr) as IMultimediaDeviceEnumerator);

            ComThread.Invoke(() =>
            {
                _systemEvents = new SystemEventNotifcationClient(() => InnerEnumerator);

                _systemEvents.DeviceAdded.Subscribe(x => OnDeviceAdded(x.DeviceId));
                _systemEvents.DeviceRemoved.Subscribe(x => OnDeviceRemoved(x.DeviceId));

                _deviceCache = new Dictionary<string, CoreAudioDevice>();

                IMultimediaDeviceCollection collection;
                InnerEnumerator.EnumAudioEndpoints(EDataFlow.All, deviceState, out collection);

                using (var coll = new MultimediaDeviceCollection(collection))
                {
                    foreach (var mDev in coll)
                        CacheDevice(mDev);
                }
            });
        }

        internal SystemEventNotifcationClient SystemEvents => _systemEvents;

        private void OnDeviceAdded(string deviceId)
        {
            var dev = GetOrAddDeviceFromRealId(deviceId);

            if (dev != null)
                OnAudioDeviceChanged(new DeviceAddedArgs(dev));
        }

        private void OnDeviceRemoved(string deviceId)
        {
            var dev = RemoveFromRealId(deviceId);
            OnAudioDeviceChanged(new DeviceRemovedArgs(dev));

        }

        ~CoreAudioController()
        {
            Dispose(false);
        }

        protected override void Dispose(bool disposing)
        {
            ComThread.BeginInvoke(() =>
            {
                _systemEvents?.Dispose();
                _systemEvents = null;
            })
            .ContinueWith(x =>
            {
                foreach (var device in _deviceCache.Values)
                {
                    device?.Dispose();
                }

                _deviceCache?.Clear();
                _lock?.Dispose();
                _innerEnumerator?.Dispose();

                base.Dispose(disposing);

                GC.SuppressFinalize(this);
            });
        }

        public override CoreAudioDevice GetDevice(Guid id, DeviceState state)
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _deviceCache.Values.FirstOrDefault(x => x.Id == id && state.HasFlag(x.State));
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        private CoreAudioDevice GetDevice(string realId)
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return
                    _deviceCache[realId];
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        private CoreAudioDevice GetOrAddDeviceFromRealId(string deviceId)
        {
            //This pre-check here may prevent more com objects from being created
            var device = GetDevice(deviceId);
            if (device != null)
                return device;

            return ComThread.Invoke(() =>
            {
                IMultimediaDevice mDevice;
                InnerEnumerator.GetDevice(deviceId, out mDevice);

                if (mDevice == null)
                    return null;

                return CacheDevice(mDevice);
            });
        }

        private CoreAudioDevice RemoveFromRealId(string deviceId)
        {
            var lockAcquired = _lock.AcquireWriteLockNonReEntrant();
            try
            {

                var deviceToRemove = _deviceCache[deviceId];

                _deviceCache.Remove(deviceId);

                return deviceToRemove;
            }
            finally
            {
                if (lockAcquired)
                    _lock.ExitWriteLock();
            }
        }

        private CoreAudioDevice CacheDevice(IMultimediaDevice mDevice)
        {
            if (!DeviceIsValid(mDevice))
                return null;

            string id;
            mDevice.GetId(out id);
            var device = GetDevice(id);

            if (device != null)
                return device;

            device = new CoreAudioDevice(mDevice, this);

            device.StateChanged.Subscribe(OnAudioDeviceChanged);
            device.DefaultChanged.Subscribe(OnAudioDeviceChanged);
            device.PropertyChanged.Subscribe(OnAudioDeviceChanged);

            var lockAcquired = _lock.AcquireWriteLockNonReEntrant();

            try
            {
                _deviceCache.Add(device.RealId, device);
                return device;
            }
            finally
            {
                if (lockAcquired)
                    _lock.ExitWriteLock();
            }
        }

        private static bool DeviceIsValid(IMultimediaDevice device)
        {
            try
            {
                string id;
                EDeviceState state;
                device.GetId(out id);
                device.GetState(out state);

                return true;
            }
            catch
            {
                return false;
            }
        }

        internal string GetDefaultDeviceId(DeviceType deviceType, Role role)
        {
            IMultimediaDevice dev;
            InnerEnumerator.GetDefaultAudioEndpoint(deviceType.AsEDataFlow(), role.AsERole(), out dev);
            if (dev == null)
                return null;

            string devId;
            dev.GetId(out devId);

            return devId;
        }

        public override CoreAudioDevice GetDefaultDevice(DeviceType deviceType, Role role)
        {
            string devId = GetDefaultDeviceId(deviceType, role);

            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _deviceCache[devId];
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }

        public override IEnumerable<CoreAudioDevice> GetDevices(DeviceType deviceType, DeviceState state)
        {
            var acquiredLock = _lock.AcquireReadLockNonReEntrant();

            try
            {
                return _deviceCache.Values.Where(x =>
                    (x.DeviceType == deviceType || deviceType == DeviceType.All)
                    && state.HasFlag(x.State)).ToList();
            }
            finally
            {
                if (acquiredLock)
                    _lock.ExitReadLock();
            }
        }
    }
}