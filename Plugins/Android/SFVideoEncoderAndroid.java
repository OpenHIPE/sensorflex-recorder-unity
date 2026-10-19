package com.sensorflex.recorder;

import android.media.Image;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.MediaMuxer;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Log;

import java.io.File;
import java.nio.ByteBuffer;
import java.util.concurrent.ArrayBlockingQueue;

// MediaCodec + MediaMuxer encoder fed from a ring of direct ByteBuffer slots.
//
// Each slot holds one tightly packed NV12 frame (Y plane, then interleaved UV).
// C# caches each slot's native address (GetDirectBufferAddress), writes a frame into
// a slot obtained from acquireSlot(), then calls submitSlot(). The encode thread copies
// the slot into the codec's input Image using that Image's own strides — so any
// YUV420 layout the device encoder wants (planar, NV12, NV21, padded rows) works —
// and returns the slot to the free list. acquireSlot() returns -1 when all slots are
// in flight, so memory stays bounded and the caller drops the frame.
public class SFVideoEncoderAndroid {
    private static final String TAG = "SFVideoEncoder";
    private static final int BIT_RATE_RGB   = 10_000_000;
    private static final int BIT_RATE_DEPTH =  2_000_000;
    private static final int FRAME_RATE     = 30;
    private static final long INPUT_WAIT_US = 10_000;     // per dequeue attempt
    private static final int  INPUT_MAX_TRIES = 100;      // ~1 s before dropping a frame

    private MediaCodec      mEncoder;
    private MediaMuxer      mMuxer;
    private String          mOutputPath;
    private int             mTrackIndex   = -1;
    private boolean         mMuxerStarted = false;
    private int             mFramesWritten = 0;
    private HandlerThread   mThread;
    private Handler         mHandler;
    private volatile boolean mFinishing   = false;
    private int mWidth, mHeight, mFrameBytes;

    private ByteBuffer[]                 mSlots;
    private ArrayBlockingQueue<Integer>  mFreeSlots;
    private byte[]                       mRowTmp;

    // codec: 0=H264, 1=HEVC  |  isDepth: depth uses low bit-rate
    public boolean start(String outputPath, int width, int height, int codec, boolean isDepth, int slotCount) {
        mOutputPath = outputPath;
        mWidth      = width;
        mHeight     = height;
        mFrameBytes = width * height * 3 / 2;
        mRowTmp     = new byte[width];

        mSlots     = new ByteBuffer[slotCount];
        mFreeSlots = new ArrayBlockingQueue<>(slotCount);
        for (int i = 0; i < slotCount; i++) {
            mSlots[i] = ByteBuffer.allocateDirect(mFrameBytes);
            mFreeSlots.offer(i);
        }

        String mime = codec == 1 ? MediaFormat.MIMETYPE_VIDEO_HEVC
                                 : MediaFormat.MIMETYPE_VIDEO_AVC;
        try {
            MediaFormat fmt = MediaFormat.createVideoFormat(mime, width, height);
            fmt.setInteger(MediaFormat.KEY_COLOR_FORMAT,
                           MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible);
            fmt.setInteger(MediaFormat.KEY_BIT_RATE,   isDepth ? BIT_RATE_DEPTH : BIT_RATE_RGB);
            fmt.setInteger(MediaFormat.KEY_FRAME_RATE, FRAME_RATE);
            fmt.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1);

            mEncoder = MediaCodec.createEncoderByType(mime);
            mEncoder.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            mMuxer   = new MediaMuxer(outputPath, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4);

            mThread  = new HandlerThread(isDepth ? "SFEncoder-Depth" : "SFEncoder-RGB");
            mThread.start();
            mHandler = new Handler(mThread.getLooper());
            mEncoder.start();
            Log.d(TAG, "[SF] " + mime + " encoder started " + width + "x" + height);
            return true;
        } catch (Exception e) {
            Log.e(TAG, "[SF] Encoder start failed: " + e.getMessage());
            releaseQuietly();
            return false;
        }
    }

    public ByteBuffer getSlot(int index) {
        return mSlots[index];
    }

    // Returns a free slot index, or -1 if every slot is in flight (caller drops the frame).
    public int acquireSlot() {
        if (mEncoder == null || mFinishing) return -1;
        Integer slot = mFreeSlots.poll();
        return slot == null ? -1 : slot;
    }

    public void releaseSlot(int slot) {
        mFreeSlots.offer(slot);
    }

    public boolean submitSlot(final int slot, final long timestampUs) {
        if (mEncoder == null || mFinishing) { mFreeSlots.offer(slot); return false; }
        mHandler.post(() -> {
            try {
                encodeFrame(mSlots[slot], timestampUs);
                drainEncoder(false);
            } catch (Exception e) {
                Log.e(TAG, "[SF] Encode failed: " + e.getMessage());
            } finally {
                mFreeSlots.offer(slot);
            }
        });
        return true;
    }

    public void finish(final IEncoderDoneListener listener) {
        mFinishing = true;
        if (mHandler == null) { if (listener != null) listener.onEncoderDone(false); return; }
        mHandler.post(() -> {
            boolean ok = true;
            try {
                if (mEncoder != null) {
                    int idx = dequeueInput();
                    if (idx >= 0) {
                        mEncoder.queueInputBuffer(idx, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM);
                        drainEncoder(true);
                    } else {
                        Log.w(TAG, "[SF] Could not queue end-of-stream; flushing what is available");
                        drainEncoder(false);
                    }
                }
            } catch (Exception e) {
                Log.e(TAG, "[SF] Finish drain failed: " + e.getMessage());
                ok = false;
            }
            ok &= cleanUp();
            mThread.quitSafely();
            if (listener != null) listener.onEncoderDone(ok);
        });
    }

    // Returns whether HEVC or H264 encoding is available on this device.
    public static boolean isCodecAvailable(String mimeType) {
        try { MediaCodec c = MediaCodec.createEncoderByType(mimeType); c.release(); return true; }
        catch (Exception e) { return false; }
    }

    // ── Encode thread ────────────────────────────────────────────────────────

    // Waits for an input buffer, draining output in between so the codec never stalls.
    private int dequeueInput() {
        for (int i = 0; i < INPUT_MAX_TRIES; i++) {
            int idx = mEncoder.dequeueInputBuffer(INPUT_WAIT_US);
            if (idx >= 0) return idx;
            drainEncoder(false);
        }
        return -1;
    }

    private void encodeFrame(ByteBuffer src, long timestampUs) {
        int idx = dequeueInput();
        if (idx < 0) { Log.w(TAG, "[SF] Input buffer unavailable, frame dropped"); return; }

        Image image = mEncoder.getInputImage(idx);
        if (image != null) {
            writeNv12(src, image);
        } else {
            // Non-flexible codec: assume semi-planar NV12 with tight rows.
            ByteBuffer in = mEncoder.getInputBuffer(idx);
            in.clear();
            ByteBuffer s = src.duplicate();
            s.clear();
            in.put(s);
        }
        mEncoder.queueInputBuffer(idx, 0, mFrameBytes, timestampUs, 0);
    }

    // Copies a tight NV12 frame into the codec's input Image, honouring its strides.
    private void writeNv12(ByteBuffer src, Image image) {
        Image.Plane[] planes = image.getPlanes();
        ByteBuffer s = src.duplicate();

        // Y
        ByteBuffer yDst = planes[0].getBuffer();
        int yRowStride = planes[0].getRowStride();
        int yPixStride = planes[0].getPixelStride();
        for (int row = 0; row < mHeight; row++) {
            s.limit(row * mWidth + mWidth).position(row * mWidth);
            if (yPixStride == 1) {
                yDst.position(row * yRowStride);
                yDst.put(s);
            } else {
                s.get(mRowTmp, 0, mWidth);
                for (int col = 0; col < mWidth; col++)
                    yDst.put(row * yRowStride + col * yPixStride, mRowTmp[col]);
            }
        }

        // UV (source interleaved U,V)
        ByteBuffer uDst = planes[1].getBuffer();
        ByteBuffer vDst = planes[2].getBuffer();
        int uRowStride = planes[1].getRowStride(), uPixStride = planes[1].getPixelStride();
        int vRowStride = planes[2].getRowStride(), vPixStride = planes[2].getPixelStride();
        int chromaW = mWidth / 2, chromaH = mHeight / 2;
        int uvBase  = mWidth * mHeight;
        s.limit(mFrameBytes);
        for (int row = 0; row < chromaH; row++) {
            s.position(uvBase + row * mWidth);
            s.get(mRowTmp, 0, mWidth);
            int uRow = row * uRowStride, vRow = row * vRowStride;
            for (int col = 0; col < chromaW; col++) {
                uDst.put(uRow + col * uPixStride, mRowTmp[col * 2]);
                vDst.put(vRow + col * vPixStride, mRowTmp[col * 2 + 1]);
            }
        }
    }

    private void drainEncoder(boolean eos) {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        int idleWaits = 0;
        while (true) {
            int idx = mEncoder.dequeueOutputBuffer(info, eos ? 100_000 : 0);
            if (idx == MediaCodec.INFO_TRY_AGAIN_LATER) {
                // At EOS, give the codec up to ~5 s of silence before giving up.
                if (!eos || ++idleWaits > 50) break;
            } else if (idx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                if (!mMuxerStarted) {
                    mTrackIndex   = mMuxer.addTrack(mEncoder.getOutputFormat());
                    mMuxer.start();
                    mMuxerStarted = true;
                }
            } else if (idx >= 0) {
                ByteBuffer data = mEncoder.getOutputBuffer(idx);
                if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0
                        && info.size > 0 && mMuxerStarted && data != null) {
                    data.position(info.offset);
                    data.limit(info.offset + info.size);
                    mMuxer.writeSampleData(mTrackIndex, data, info);
                    mFramesWritten++;
                }
                mEncoder.releaseOutputBuffer(idx, false);
                if ((info.flags & MediaCodec.BUFFER_FLAG_END_OF_STREAM) != 0) break;
            }
        }
    }

    // Returns true if a playable MP4 was written.
    private boolean cleanUp() {
        boolean ok = mMuxerStarted && mFramesWritten > 0;
        try {
            if (mEncoder != null) { mEncoder.stop(); mEncoder.release(); mEncoder = null; }
        } catch (Exception e) { Log.e(TAG, "[SF] Encoder cleanup error: " + e.getMessage()); ok = false; }
        try {
            if (mMuxer != null) {
                if (mMuxerStarted) mMuxer.stop();   // stop() throws if the muxer never started
                mMuxer.release();
                mMuxer = null;
            }
        } catch (Exception e) { Log.e(TAG, "[SF] Muxer cleanup error: " + e.getMessage()); ok = false; }
        if (!ok) {
            Log.w(TAG, "[SF] No frames encoded; removing " + mOutputPath);
            new File(mOutputPath).delete();
        }
        Log.d(TAG, "[SF] Encoder finished, frames=" + mFramesWritten);
        return ok;
    }

    private void releaseQuietly() {
        try { if (mEncoder != null) mEncoder.release(); } catch (Exception ignored) { }
        try { if (mMuxer   != null) mMuxer.release();   } catch (Exception ignored) { }
        mEncoder = null;
        mMuxer   = null;
        if (mThread != null) mThread.quitSafely();
    }
}
