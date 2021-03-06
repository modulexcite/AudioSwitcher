﻿using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioSwitcher.AudioApi.CoreAudio.Interfaces;
using AudioSwitcher.AudioApi.CoreAudio.Threading;
using AudioSwitcher.AudioApi.Observables;
using AudioSwitcher.AudioApi.Session;

namespace AudioSwitcher.AudioApi.CoreAudio
{
    internal sealed class CoreAudioSession : IAudioSession, IAudioSessionEvents, IDisposable
    {
        private readonly IDisposable _deviceMutedSubscription;
        private readonly IAudioMeterInformation _meterInformation;
        private readonly ISimpleAudioVolume _simpleAudioVolume;
        private readonly IDisposable _timerSubscription;
        private readonly Broadcaster<SessionDisconnectedArgs> _disconnected;
        private readonly Broadcaster<SessionMuteChangedArgs> _muteChanged;
        private readonly Broadcaster<SessionPeakValueChangedArgs> _peakValueChanged;
        private readonly Broadcaster<SessionStateChangedArgs> _stateChanged;
        private readonly Broadcaster<SessionVolumeChangedArgs> _volumeChanged;
        private IAudioSessionControl2 _audioSessionControl;
        private string _displayName;
        private string _executablePath;
        private string _fileDescription;
        private string _iconPath;
        private string _id;
        private bool _isDisposed;
        private bool _isMuted;
        private bool _isSystemSession;

        private float _peakValue = -1;
        private int _processId;
        private AudioSessionState _state;
        private double _volume;
        private bool _isUpdatingPeakValue;

        public CoreAudioSession(CoreAudioDevice device, IAudioSessionControl control)
        {
            ComThread.Assert();

            Device = device;

            _deviceMutedSubscription = Device.MuteChanged.Subscribe(x =>
            {
                OnMuteChanged(_isMuted);
            });


            // ReSharper disable once SuspiciousTypeConversion.Global
            _audioSessionControl = control as IAudioSessionControl2;

            // ReSharper disable once SuspiciousTypeConversion.Global
            _simpleAudioVolume = control as ISimpleAudioVolume;

            // ReSharper disable once SuspiciousTypeConversion.Global
            _meterInformation = control as IAudioMeterInformation;

            if (_audioSessionControl == null || _simpleAudioVolume == null)
                throw new InvalidComObjectException("control");

            if (_meterInformation != null)
            {
                //start a timer to poll for peak value changes
                _timerSubscription = PeakValueTimer.PeakValueTick.Subscribe(Timer_UpdatePeakValue);
            }

            _stateChanged = new Broadcaster<SessionStateChangedArgs>();
            _disconnected = new Broadcaster<SessionDisconnectedArgs>();
            _volumeChanged = new Broadcaster<SessionVolumeChangedArgs>();
            _muteChanged = new Broadcaster<SessionMuteChangedArgs>();
            _peakValueChanged = new Broadcaster<SessionPeakValueChangedArgs>();

            _audioSessionControl.RegisterAudioSessionNotification(this);

            RefreshProperties();
            RefreshVolume();
        }

        public IDevice Device { get; private set; }

        public IObservable<SessionVolumeChangedArgs> VolumeChanged
        {
            get { return _volumeChanged.AsObservable(); }
        }

        public IObservable<SessionPeakValueChangedArgs> PeakValueChanged
        {
            get { return _peakValueChanged.AsObservable(); }
        }

        public IObservable<SessionMuteChangedArgs> MuteChanged
        {
            get { return _muteChanged.AsObservable(); }
        }

        public IObservable<SessionStateChangedArgs> StateChanged
        {
            get { return _stateChanged.AsObservable(); }
        }

        public IObservable<SessionDisconnectedArgs> Disconnected
        {
            get { return _disconnected.AsObservable(); }
        }

        public string Id
        {
            get { return _id; }
        }

        public int ProcessId
        {
            get { return _processId; }
        }

        public string DisplayName
        {
            get { return String.IsNullOrWhiteSpace(_displayName) ? _fileDescription : _displayName; }
        }

        public string IconPath
        {
            get { return _iconPath; }
        }

        public string ExecutablePath
        {
            get { return _executablePath; }
        }

        public bool IsSystemSession
        {
            get { return _isSystemSession; }
        }

        public double Volume
        {
            get
            {
                return _volume;
            }
            set
            {
                if (_isDisposed)
                    return;

                ComThread.Invoke(() => _simpleAudioVolume.SetMasterVolume((float)(value / 100), Guid.Empty));
                _volume = value;
                OnVolumeChanged(_volume);
            }
        }

        public bool IsMuted
        {
            get
            {
                return _isMuted || Device.IsMuted;
            }
            set
            {
                if (_isMuted == value || _isDisposed)
                    return;

                ComThread.Invoke(() => _simpleAudioVolume.SetMute(value, Guid.Empty));

                _isMuted = value;
                OnMuteChanged(IsMuted);
            }
        }

        public AudioSessionState SessionState
        {
            get { return _state; }
        }

        int IAudioSessionEvents.OnDisplayNameChanged(string displayName, ref Guid eventContext)
        {
            _displayName = displayName;
            return 0;
        }

        int IAudioSessionEvents.OnIconPathChanged(string iconPath, ref Guid eventContext)
        {
            _iconPath = iconPath;
            return 0;
        }

        int IAudioSessionEvents.OnSimpleVolumeChanged(float volume, bool isMuted, ref Guid eventContext)
        {
            if (Math.Abs(_volume - volume * 100) > 0)
            {
                _volume = volume * 100;
                OnVolumeChanged(_volume);
            }

            if (isMuted != _isMuted)
            {
                _isMuted = isMuted;
                OnMuteChanged(_isMuted);
            }

            return 0;
        }

        int IAudioSessionEvents.OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex, ref Guid eventContext)
        {
            return 0;
        }

        int IAudioSessionEvents.OnGroupingParamChanged(ref Guid groupingId, ref Guid eventContext)
        {
            return 0;
        }

        int IAudioSessionEvents.OnSessionDisconnected(EAudioSessionDisconnectReason disconnectReason)
        {
            OnDisconnected();
            return 0;
        }

        int IAudioSessionEvents.OnStateChanged(EAudioSessionState state)
        {
            _state = state.AsAudioSessionState();
            OnStateChanged(state);
            return 0;
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            if (_timerSubscription != null)
                _timerSubscription.Dispose();

            _deviceMutedSubscription.Dispose();
            _muteChanged.Dispose();
            _stateChanged.Dispose();
            _disconnected.Dispose();
            _volumeChanged.Dispose();
            _peakValueChanged.Dispose();

            //Run this on the com thread to ensure it's diposed correctly
            ComThread.BeginInvoke(() =>
            {
                _audioSessionControl.UnregisterAudioSessionNotification(this);
            }).ContinueWith(x =>
            {
                _audioSessionControl = null;
            });

            GC.SuppressFinalize(this);

            _isDisposed = true;
        }

        private void Timer_UpdatePeakValue(long ticks)
        {
            if (_isUpdatingPeakValue)
                return;

            _isUpdatingPeakValue = true;

            float peakValue = _peakValue;

            //ComThread.BeginInvoke(() =>
            //{
                if (_isDisposed)
                    return;

                try
                {
                    if (_meterInformation == null)
                        return;

                    _meterInformation.GetPeakValue(out peakValue);
                }
                catch (InvalidComObjectException)
                {
                    //ignored - usually means the com object has been released, but the timer is still ticking
                }
            //})
            //.ContinueWith(x =>
            //{
                if (Math.Abs(_peakValue - peakValue) > 0.001)
                {
                    _peakValue = peakValue;
                    OnPeakValueChanged(peakValue * 100);
                }

                _isUpdatingPeakValue = false;
            //});
        }

        ~CoreAudioSession()
        {
            Dispose();
        }

        private void RefreshVolume()
        {
            if (_isDisposed)
                return;

            ComThread.Invoke(() =>
            {
                float vol;
                _simpleAudioVolume.GetMasterVolume(out vol);
                _volume = vol * 100;

                bool isMuted;
                _simpleAudioVolume.GetMute(out isMuted);

                _isMuted = isMuted;
            });
        }

        private void RefreshProperties()
        {
            if (_isDisposed)
                return;

            ComThread.Invoke(() =>
            {
                _isSystemSession = _audioSessionControl.IsSystemSoundsSession() == 0;
                _audioSessionControl.GetDisplayName(out _displayName);

                _audioSessionControl.GetIconPath(out _iconPath);

                EAudioSessionState state;
                _audioSessionControl.GetState(out state);
                _state = state.AsAudioSessionState();

                uint processId;
                _audioSessionControl.GetProcessId(out processId);
                _processId = (int)processId;

                _audioSessionControl.GetSessionIdentifier(out _id);

                try
                {
                    if (ProcessId > 0)
                    {
                        var proc = Process.GetProcessById(ProcessId);
                        _executablePath = proc.MainModule.FileName;
                        _fileDescription = proc.MainModule.FileVersionInfo.FileDescription;
                    }
                }
                catch
                {
                    _fileDescription = "";
                }
            });
        }

        private void OnVolumeChanged(double volume)
        {
            _volumeChanged.OnNext(new SessionVolumeChangedArgs(this, volume));
        }

        private void OnStateChanged(EAudioSessionState state)
        {
            _stateChanged.OnNext(new SessionStateChangedArgs(this, state.AsAudioSessionState()));
        }

        private void OnDisconnected()
        {
            _disconnected.OnNext(new SessionDisconnectedArgs(this));
        }

        private void OnMuteChanged(bool isMuted)
        {
            _muteChanged.OnNext(new SessionMuteChangedArgs(this, isMuted));
        }

        private void OnPeakValueChanged(double peakValue)
        {
            _peakValueChanged.OnNext(new SessionPeakValueChangedArgs(this, peakValue));
        }
    }
}
