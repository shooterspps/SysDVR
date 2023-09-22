﻿using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static SDL2.SDL;
using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;
using System.Threading;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using SysDVR.Client.Windows;
using SysDVR.Client.Core;
using SysDVR.Client.GUI;
using ImGuiNET;
using SDL2;

namespace SysDVR.Client.Targets.Player
{
    unsafe class DecoderContext
    {
        public AVCodecContext* CodecCtx { get; init; }

        public AVFrame* RenderFrame;
        public AVFrame* ReceiveFrame;

        public AVFrame* Frame1 { get; init; }
        public AVFrame* Frame2 { get; init; }

        public object CodecLock { get; init; }

        public StreamSynchronizationHelper SyncHelper;
        public AutoResetEvent OnFrameEvent;
    }

    unsafe struct FormatConverterContext
    {
        public SwsContext* Converter { get; init; }
        public AVFrame* Frame { get; init; }
    }

    class PlayerManager : BaseStreamManager
    {
        public new H264StreamTarget VideoTarget;
        public new AudioStreamTarget AudioTarget;

        public PlayerManager(bool HasVideo, bool HasAudio, CancellationTokenSource cancel) : base(
            HasVideo ? new H264StreamTarget() : null,
            HasAudio ? new AudioStreamTarget() : null,
            cancel)
        {
            VideoTarget = base.VideoTarget as H264StreamTarget;
            AudioTarget = base.AudioTarget as AudioStreamTarget;
        }
    }

    class AudioPlayer : IDisposable
    {
        uint DeviceID;

        public AudioPlayer(AudioStreamTarget target) 
        {
            Program.Instance.BugCheckThreadId();

            SDL_AudioSpec wantedSpec = new SDL_AudioSpec()
            {
                channels = StreamInfo.AudioChannels,
                format = AUDIO_S16LSB,
                freq = StreamInfo.AudioSampleRate,
                // StreamInfo.MinAudioSamplesPerPayload * 2 was the default until sysdvr 5.4
                // however SDL will pick its preferred buffer size since we pass SDL_AUDIO_ALLOW_SAMPLES_CHANGE,
                // this is fine since we have our own buffering.
                samples = StreamInfo.MinAudioSamplesPerPayload,
                callback = IntPtr.Zero,
            };

            DeviceID = SDL_OpenAudioDevice(IntPtr.Zero, 0, ref wantedSpec, out var obtained, (int)SDL_AUDIO_ALLOW_SAMPLES_CHANGE);

            DeviceID.AssertNotZero(SDL_GetError);
            target.DeviceId = DeviceID;

            if (DebugOptions.Current.Log)
                Console.WriteLine($"SDL_Audio: requested samples per callback={wantedSpec.samples} obtained={obtained.samples}");
        }

        public void Pause() 
        {
            SDL_PauseAudioDevice(DeviceID, 1);
        }

        public void Resume() 
        {
            SDL_PauseAudioDevice(DeviceID, 0);
        }

        public void Dispose()
        {
            Pause();
            SDL_CloseAudioDevice(DeviceID);
        }
    }

    class VideoPlayer : IDisposable
    {
        public const AVPixelFormat TargetDecodingFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
        public readonly uint TargetTextureFormat = SDL_PIXELFORMAT_IYUV;

        public DecoderContext Decoder { get; private set; }
        FormatConverterContext Converter; // Initialized only when the decoder output format doesn't match the SDL texture format

        public string DecoderName { get; private set; }
        public bool AcceleratedDecotr { get; private set; }

        public object TextureLock;
        public IntPtr TargetTexture;
        public SDL_Rect TargetTextureSize;

        public VideoPlayer(string? preferredDecoderName, bool hwAccel)
        {
            InitVideoDecoder(preferredDecoderName, hwAccel);
            InitSDLRenderTexture();
        }

        void InitSDLRenderTexture()
        {
            Program.Instance.BugCheckThreadId();

            var tex = SDL_CreateTexture(Program.Instance.SdlRenderer, TargetTextureFormat,
                (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
                StreamInfo.VideoWidth, StreamInfo.VideoHeight).AssertNotNull(SDL_GetError);

            if (DebugOptions.Current.Log)
            {
                var pixfmt = SDL_QueryTexture(tex, out var format, out var a, out var w, out var h);
                Console.WriteLine($"SDL texture info: f = {SDL_GetPixelFormatName(format)} a = {a} w = {w} h = {h}");

                SDL_RendererInfo info;
                SDL_GetRendererInfo(Program.Instance.SdlRenderer, out info);
                for (int i = 0; i < info.num_texture_formats; i++) unsafe
                    {
                        Console.WriteLine($"Renderer supports pixel format {SDL_GetPixelFormatName(info.texture_formats[i])}");
                    }
            }

            TargetTextureSize = new SDL_Rect() { x = 0, y = 0, w = StreamInfo.VideoWidth, h = StreamInfo.VideoHeight };
            TargetTexture = tex;
            TextureLock = new object();
        }

        unsafe void InitVideoDecoder(string? name, bool useHwAcc)
        {
            AVCodec* codec = null;

            if (name is not null)
            {
                codec = avcodec_find_decoder_by_name(name);
            }
            
            if (codec == null && useHwAcc)
            {
                name = LibavUtils.GetH264Decoders().Where(x => x.Name != "h264").FirstOrDefault()?.Name;

                if (name != null)
                {
                    codec = avcodec_find_decoder_by_name(name);
                    if (codec != null)
                    {
                        AcceleratedDecotr = true;
                    }
                }
            }

            if (codec == null)
                codec = avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);

            if (codec == null)
                throw new Exception("Couldn't find any compatible video codecs");

            Decoder = CreateDecoderContext(codec);
            DecoderName = Marshal.PtrToStringAnsi((IntPtr)codec->name);
        }

        static unsafe DecoderContext CreateDecoderContext(AVCodec* codec)
        {
            if (codec == null)
                throw new Exception("Codec can't be null");

            string codecName = Marshal.PtrToStringAnsi((IntPtr)codec->name);

            Console.WriteLine($"Initializing video player with {codecName} codec.");

            var codectx = avcodec_alloc_context3(codec);
            if (codectx == null)
                throw new Exception("Couldn't allocate a codec context");

            // These are set in ffplay
            codectx->codec_id = codec->id;
            codectx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            codectx->bit_rate = 0;

            // Some decoders break without this
            codectx->width = StreamInfo.VideoWidth;
            codectx->height = StreamInfo.VideoHeight;

            var (ex, sz) = LibavUtils.AllocateH264Extradata();
            codectx->extradata_size = sz;
            codectx->extradata = (byte*)ex.ToPointer();

            avcodec_open2(codectx, codec, null).AssertZero("Couldn't open the codec.");

            var pic = av_frame_alloc();
            if (pic == null)
                throw new Exception("Couldn't allocate the decoding frame");

            var pic2 = av_frame_alloc();
            if (pic2 == null)
                throw new Exception("Couldn't allocate the decoding frame");

            return new DecoderContext()
            {
                CodecCtx = codectx,
                Frame1 = pic,
                Frame2 = pic2,
                ReceiveFrame = pic,
                RenderFrame = pic2,
                CodecLock = new object(),
                OnFrameEvent = new AutoResetEvent(true)
            };
        }

        public unsafe bool DecodeFrame() 
        {
            if (DecodeFrameInternal())
            {
                // TODO: this call is needed only with opengl on linux (and not on every linux install i tested) where TextureUpdate must be called by the main thread,
                // Check if are there any performance improvements by moving this to the decoder thread on other OSes
                UpdateSDLTexture(Decoder.RenderFrame);

                return true;
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int av_ceil_rshift(int a, int b) =>
            -(-a >> b);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe void UpdateSDLTexture(AVFrame* pic)
        {
            if (pic->linesize[0] > 0 && pic->linesize[1] > 0 && pic->linesize[2] > 0)
            {
                SDL_UpdateYUVTexture(TargetTexture, ref TargetTextureSize,
                    (IntPtr)pic->data[0], pic->linesize[0],
                    (IntPtr)pic->data[1], pic->linesize[1],
                    (IntPtr)pic->data[2], pic->linesize[2]);
            }
#if DEBUG
            // Not sure if this is needed but ffplay source does handle this case, all my tests had positive linesize
            else if (pic->linesize[0] < 0 && pic->linesize[1] < 0 && pic->linesize[2] < 0)
            {
                Console.WriteLine("Negative Linesize");
                SDL_UpdateYUVTexture(TargetTexture, ref TargetTextureSize,
                    (IntPtr)(pic->data[0] + pic->linesize[0] * (pic->height - 1)), -pic->linesize[0],
                    (IntPtr)(pic->data[1] + pic->linesize[1] * (av_ceil_rshift(pic->height, 1) - 1)), -pic->linesize[1],
                    (IntPtr)(pic->data[2] + pic->linesize[2] * (av_ceil_rshift(pic->height, 1) - 1)), -pic->linesize[2]);
            }
#endif
            // While this doesn't seem to be handled in ffplay but the texture can be non-planar with some decoders
            else if (pic->linesize[0] > 0 && pic->linesize[1] == 0)
            {
                SDL_UpdateTexture(TargetTexture, ref TargetTextureSize, (nint)pic->data[0], pic->linesize[0]);
            }
            else Console.WriteLine($"Error: Non-positive planar linesizes are not supported, open an issue on Github. {pic->linesize[0]} {pic->linesize[1]} {pic->linesize[2]}");
        }

        bool converterFirstFrameCheck = false;
        unsafe bool DecodeFrameInternal()
        {
            int ret = 0;

            lock (Decoder.CodecLock)
                ret = avcodec_receive_frame(Decoder.CodecCtx, Decoder.ReceiveFrame);

            if (ret == AVERROR(EAGAIN))
            {
                // Try again for the next SDL frame
                return false;
            }
            else if (ret != 0)
            {
                // Should not happen
                Console.WriteLine($"avcodec_receive_frame {ret}");
                return false;
            }
            else
            {
                // On the first frame we get check if we need to use a converter
                if (!converterFirstFrameCheck && Decoder.CodecCtx->pix_fmt != AVPixelFormat.AV_PIX_FMT_NONE)
                {
                    if (DebugOptions.Current.Log)
                        Console.WriteLine($"Decoder.CodecCtx uses pixel format {Decoder.CodecCtx->pix_fmt}");

                    converterFirstFrameCheck = true;
                    if (Decoder.CodecCtx->pix_fmt != TargetDecodingFormat)
                    {
                        Converter = InitializeConverter(Decoder.CodecCtx);
                        // Render to the converted frame
                        Decoder.RenderFrame = Converter.Frame;
                    }
                }

                if (Converter.Converter != null)
                {
                    var source = Decoder.ReceiveFrame;
                    var target = Decoder.RenderFrame;
                    sws_scale(Converter.Converter, source->data, source->linesize, 0, source->height, target->data, target->linesize);
                }
                else
                {
                    // Swap the frames so we can render source
                    var toRender = Decoder.ReceiveFrame;
                    var receiveNext = Decoder.RenderFrame;

                    Decoder.ReceiveFrame = receiveNext;
                    Decoder.RenderFrame = toRender;
                }

                return true;
            }
        }

        unsafe static FormatConverterContext InitializeConverter(AVCodecContext* codecctx)
        {
            AVFrame* dstframe = null;
            SwsContext* swsContext = null;

            Console.WriteLine($"Initializing converter for {codecctx->pix_fmt}");

            dstframe = av_frame_alloc();

            if (dstframe == null)
                throw new Exception("Couldn't allocate the the converted frame");

            dstframe->format = (int)TargetDecodingFormat;
            dstframe->width = StreamInfo.VideoWidth;
            dstframe->height = StreamInfo.VideoHeight;

            av_frame_get_buffer(dstframe, 32).AssertZero("Couldn't allocate the buffer for the converted frame");

            swsContext = sws_getContext(codecctx->width, codecctx->height, codecctx->pix_fmt,
                                        dstframe->width, dstframe->height, (AVPixelFormat)dstframe->format,
                                        SWS_FAST_BILINEAR, null, null, null);

            if (swsContext == null)
                throw new Exception("Couldn't initialize the converter");

            return new FormatConverterContext()
            {
                Converter = swsContext,
                Frame = dstframe
            };
        }

        public unsafe void Dispose()
        {
            var ptr = Decoder.Frame1;
            av_frame_free(&ptr);

            ptr = Decoder.Frame2;
            av_frame_free(&ptr);

            var ptr2 = Decoder.CodecCtx;
            avcodec_free_context(&ptr2);

            if (Converter.Converter != null)
            {
                var ptr3 = Converter.Frame;
                av_frame_free(&ptr3);

                sws_freeContext(Converter.Converter);
            }

            if (TargetTexture != 0)
                SDL_DestroyTexture(TargetTexture);

            Decoder.OnFrameEvent.Dispose();
        }
    }
}
