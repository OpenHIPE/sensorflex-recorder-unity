package com.sensorflex.recorder;

import android.content.Context;
import android.graphics.ImageFormat;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.Log;
import android.util.Range;

import java.nio.ByteBuffer;
import java.util.Collections;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

// Records an Android XR headset's world-facing camera (Camera2) into an MP4 through
// SFVideoEncoderAndroid. Each accepted frame's capture time is reported as CLOCK_MONOTONIC
// nanoseconds, in encode order, so C# maps video frame N to timestamp N exactly.
//
// Camera timestamps on these devices use SENSOR_INFO_TIMESTAMP_SOURCE_REALTIME (boot clock);
// they are moved to the monotonic clock with the current (boot - monotonic) offset.
public class SFXrCameraRecorder {
    private static final String TAG = "SFXrCamera";
    private static final int SLOTS = 6;

    private HandlerThread mThread;
    private Handler mHandler;
    private CameraDevice mDevice;
    private CameraCaptureSession mSession;
    private ImageReader mReader;
    private SFVideoEncoderAndroid mEncoder;
    private int mWidth, mHeight;
    private long mFirstMonoNs = -1;
    private volatile boolean mStopping;

    // Monotonic timestamps of frames handed to the encoder, not yet drained by C#.
    private final long[] mPending = new long[256];
    private int mPendingCount;
    private int mDropped;
    private boolean mRealtimeSource = true;
    private byte[] mRowU, mRowV, mRowOut;   // chroma row scratch (camera thread)

    // codec: 0=H264, 1=HEVC. Returns false if the camera or encoder could not start.
    public boolean start(Context ctx, String cameraId, String mp4Path, int width, int height, int codec) {
        mWidth = width;
        mHeight = height;
        mThread = new HandlerThread("SFXrCamera");
        mThread.start();
        mHandler = new Handler(mThread.getLooper());

        mEncoder = new SFVideoEncoderAndroid();
        if (!mEncoder.start(mp4Path, width, height, codec, false, SLOTS)) {
            Log.e(TAG, "[SF] encoder start failed");
            return false;
        }

        try {
            CameraManager cm = (CameraManager) ctx.getSystemService(Context.CAMERA_SERVICE);
            CameraCharacteristics c = cm.getCameraCharacteristics(cameraId);
            Integer src = c.get(CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE);
            mRealtimeSource = src != null && src == CameraCharacteristics.SENSOR_INFO_TIMESTAMP_SOURCE_REALTIME;

            mReader = ImageReader.newInstance(width, height, ImageFormat.YUV_420_888, 4);
            mReader.setOnImageAvailableListener(this::onImage, mHandler);

            final CountDownLatch opened = new CountDownLatch(1);
            cm.openCamera(cameraId, new CameraDevice.StateCallback() {
                @Override public void onOpened(CameraDevice d) { mDevice = d; opened.countDown(); }
                @Override public void onDisconnected(CameraDevice d) { Log.w(TAG, "[SF] camera disconnected"); d.close(); opened.countDown(); }
                @Override public void onError(CameraDevice d, int e) { Log.e(TAG, "[SF] camera error " + e); d.close(); opened.countDown(); }
            }, mHandler);
            if (!opened.await(5, TimeUnit.SECONDS) || mDevice == null) { Log.e(TAG, "[SF] camera open failed"); return false; }

            final CountDownLatch configured = new CountDownLatch(1);
            mDevice.createCaptureSession(Collections.singletonList(mReader.getSurface()), new CameraCaptureSession.StateCallback() {
                @Override public void onConfigured(CameraCaptureSession s) { mSession = s; configured.countDown(); }
                @Override public void onConfigureFailed(CameraCaptureSession s) { configured.countDown(); }
            }, mHandler);
            if (!configured.await(5, TimeUnit.SECONDS) || mSession == null) { Log.e(TAG, "[SF] session config failed"); return false; }

            CaptureRequest.Builder b = mDevice.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
            b.addTarget(mReader.getSurface());
            Range<Integer>[] ranges = c.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
            if (ranges != null && ranges.length > 0) {
                Range<Integer> best = ranges[0];
                for (Range<Integer> r : ranges)
                    if (r.getUpper() > best.getUpper() || (r.getUpper().equals(best.getUpper()) && r.getLower() > best.getLower())) best = r;
                b.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, best);
            }
            mSession.setRepeatingRequest(b.build(), null, mHandler);
            Log.d(TAG, "[SF] camera " + cameraId + " recording " + width + "x" + height + " realtimeTs=" + mRealtimeSource);
            return true;
        } catch (SecurityException se) {
            Log.e(TAG, "[SF] CAMERA permission missing: " + se.getMessage());
            return false;
        } catch (Exception e) {
            Log.e(TAG, "[SF] camera start failed: " + e);
            return false;
        }
    }

    private void onImage(ImageReader reader) {
        Image img = reader.acquireLatestImage();
        if (img == null) return;
        try {
            if (mStopping) return;
            long ts = img.getTimestamp();
            long monoNs = mRealtimeSource ? ts - (SystemClock.elapsedRealtimeNanos() - System.nanoTime()) : ts;

            int slot = mEncoder.acquireSlot();
            if (slot < 0) { synchronized (mPending) { mDropped++; } return; }
            copyToNv12(img, mEncoder.getSlot(slot));
            if (mFirstMonoNs < 0) mFirstMonoNs = monoNs;
            if (!mEncoder.submitSlot(slot, (monoNs - mFirstMonoNs) / 1000L)) { synchronized (mPending) { mDropped++; } return; }
            synchronized (mPending) {
                if (mPendingCount < mPending.length) mPending[mPendingCount++] = monoNs;
                else mDropped++;   // C# stopped draining; should not happen
            }
        } catch (Exception e) {
            Log.e(TAG, "[SF] frame failed: " + e);
        } finally {
            img.close();
        }
    }

    // YUV_420_888 → tightly packed NV12 in the slot buffer.
    private void copyToNv12(Image img, ByteBuffer dst) {
        Image.Plane[] p = img.getPlanes();
        int w = mWidth, h = mHeight;
        dst.clear();

        ByteBuffer y = p[0].getBuffer();
        int yRs = p[0].getRowStride();
        ByteBuffer r = y.duplicate();
        for (int row = 0; row < h; row++) {
            r.limit(row * yRs + w);
            r.position(row * yRs);
            dst.position(row * w);
            dst.put(r);
        }

        ByteBuffer u = p[1].getBuffer(), v = p[2].getBuffer();
        int uRs = p[1].getRowStride(), uPs = p[1].getPixelStride();
        int vRs = p[2].getRowStride(), vPs = p[2].getPixelStride();
        int base = w * h;

        // Chroma: bulk-read each U and V row into arrays, interleave in plain arrays, bulk-write.
        // (Per-byte ByteBuffer access is too slow for 24 fps at 1920x1440.)
        if (mRowU == null || mRowU.length < uRs) mRowU = new byte[uRs];
        if (mRowV == null || mRowV.length < vRs) mRowV = new byte[vRs];
        if (mRowOut == null || mRowOut.length < w) mRowOut = new byte[w];
        ByteBuffer ud = u.duplicate(), vd = v.duplicate();
        int cw = w / 2;
        for (int row = 0; row < h / 2; row++) {
            int un = Math.min(uRs, ud.capacity() - row * uRs);
            int vn = Math.min(vRs, vd.capacity() - row * vRs);
            ud.position(row * uRs); ud.get(mRowU, 0, un);
            vd.position(row * vRs); vd.get(mRowV, 0, vn);
            for (int col = 0; col < cw; col++) {
                mRowOut[col * 2]     = mRowU[col * uPs];
                mRowOut[col * 2 + 1] = mRowV[col * vPs];
            }
            dst.position(base + row * w);
            dst.put(mRowOut, 0, w);
        }
    }

    // Monotonic timestamps (ns) of frames encoded since the last call, in order.
    public long[] drainTimestamps() {
        synchronized (mPending) {
            long[] out = new long[mPendingCount];
            System.arraycopy(mPending, 0, out, 0, mPendingCount);
            mPendingCount = 0;
            return out;
        }
    }

    public int droppedFrames() { synchronized (mPending) { return mDropped; } }

    // Nominal pinhole intrinsics {fx, fy, cx, cy} at width×height from the lens focal length
    // and sensor size (the camera publishes no calibration). The output is assumed to be the
    // centred crop of the pixel array with the output's aspect ratio, scaled to width×height.
    public static float[] nominalIntrinsics(Context ctx, String cameraId, int width, int height) {
        try {
            CameraManager cm = (CameraManager) ctx.getSystemService(Context.CAMERA_SERVICE);
            CameraCharacteristics c = cm.getCameraCharacteristics(cameraId);
            float[] focals = c.get(CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS);
            android.util.SizeF phys = c.get(CameraCharacteristics.SENSOR_INFO_PHYSICAL_SIZE);
            android.util.Size arr = c.get(CameraCharacteristics.SENSOR_INFO_PIXEL_ARRAY_SIZE);
            if (focals == null || focals.length == 0 || phys == null || arr == null) return null;
            float fPx = focals[0] / phys.getWidth() * arr.getWidth();          // focal length in sensor pixels
            float cropW = Math.min(arr.getWidth(), arr.getHeight() * (float) width / height);
            float scale = width / cropW;                                       // sensor pixel → output pixel
            return new float[] { fPx * scale, fPx * scale, width * 0.5f, height * 0.5f };
        } catch (Exception e) {
            Log.w(TAG, "[SF] nominal intrinsics unavailable: " + e);
            return null;
        }
    }

    public void stop(final IEncoderDoneListener listener) {
        mStopping = true;
        try { if (mSession != null) { mSession.stopRepeating(); mSession.close(); } } catch (Exception ignored) { }
        if (mDevice != null) mDevice.close();
        if (mReader != null) mReader.close();
        if (mThread != null) mThread.quitSafely();
        if (mEncoder != null) mEncoder.finish(listener);
        else if (listener != null) listener.onEncoderDone(false);
    }
}
