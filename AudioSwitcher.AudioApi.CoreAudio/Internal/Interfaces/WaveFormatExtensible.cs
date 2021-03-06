﻿using System;
using System.Runtime.InteropServices;

namespace AudioSwitcher.AudioApi.CoreAudio.Interfaces
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 2)]
    public class WaveFormatExtensible : WaveFormat
    {
        private readonly int dwChannelMask;
        private readonly Guid subFormat;
        private readonly short wValidBitsPerSample;

        public short ValidBitsPerSample
        {
            get
            {
                return wValidBitsPerSample;
            }
        }

        public SpeakerConfiguration ChannelMask
        {
            get
            {
                return (SpeakerConfiguration)dwChannelMask;
            }
        }

        public Guid SubFormat
        {
            get
            {
                return subFormat;
            }
        }

        /// <summary>
        /// Parameterless constructor for marshalling
        /// </summary>
        private WaveFormatExtensible()
        {
        }

        /// <summary>
        /// Creates a new WaveFormatExtensible for PCM
        /// KSDATAFORMAT_SUBTYPE_PCM
        /// </summary>
        public WaveFormatExtensible(SampleRate rate, BitDepth bits, SpeakerConfiguration channelMask)
            : this(rate, bits, channelMask, new Guid("00000001-0000-0010-8000-00AA00389B71"))
        {
            wValidBitsPerSample = (short)bits;
            dwChannelMask = (int)channelMask;
        }

        public WaveFormatExtensible(SampleRate rate, BitDepth bits, SpeakerConfiguration channelMask, Guid subFormat)
            : base(rate, bits, channelMask, WaveFormatEncoding.Extensible, Marshal.SizeOf(typeof(WaveFormatExtensible)))
        {
            wValidBitsPerSample = (short)bits;
            dwChannelMask = (int)channelMask;

            this.subFormat = subFormat;
        }

        public override string ToString()
        {
            return String.Format("{0} wBitsPerSample:{1} dwChannelMask:{2} subFormat:{3} extraSize:{4}",
                base.ToString(),
                wValidBitsPerSample,
                dwChannelMask,
                subFormat,
                ExtraSize);
        }
    }
}
