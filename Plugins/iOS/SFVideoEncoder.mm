// SFVideoEncoder.mm
// Hardware HEVC/H264 encoding for SensorFlex Recorder — RGB + Depth video paths.
// LZ4 depth writer using Apple Compression framework.
//
// RGB: YCbCr420 (BiPlanar, FullRange) via AVFoundation + VideoToolbox → rgb.mp4
//      codec param: 0=H264, 1=HEVC
//
// Depth LZ4: float32 metres → COMPRESSION_LZ4_RAW → depth.bin (raw concatenated blocks)
//            Compressed byte count per frame → depth_sizes.bin (int32 per frame, little-endian)
//            depth.bin is compatible with K4os.Compression.LZ4 LZ4Codec.Decode on the player side.
//
// Depth Video: float32 metres → HEVC (kCVPixelFormatType_OneComponent16Half via vImage float16)
//                              or H264 (kCVPixelFormatType_420YpCbCr8BiPlanarFullRange via uint8 norm)
//              → depth.mp4
//
// Timestamps passed from C# are session-relative nanoseconds (first frame = 0 ns).

#import <AVFoundation/AVFoundation.h>
#include <compression.h>
#include <Accelerate/Accelerate.h>

// COMPRESSION_ZSTD was added in iOS 13 SDK; define the numeric value for older SDK builds.
#ifndef COMPRESSION_ZSTD
#define COMPRESSION_ZSTD ((compression_algorithm)0x505)
#endif

// ─── Shared types ─────────────────────────────────────────────────────────────

typedef void (*SFEncoderDoneCallback)(int success);

typedef struct {
    AVAssetWriter                          *writer;
    AVAssetWriterInput                     *input;
    AVAssetWriterInputPixelBufferAdaptor   *adaptor;
} SFEncoderSession;

// ─── RGB encoder ──────────────────────────────────────────────────────────────
//
// All AVAssetWriter/VideoToolbox work runs on gRgbQueue (serial background).
// The main thread only copies pixel data into a CVPixelBuffer (~2 ms), then
// dispatches the encode. This eliminates the ~1 s freeze caused by AVAssetWriter
// startWriting + VideoToolbox HEVC cold start on the first frame callback.

static SFEncoderSession  gRgb      = {};
static volatile BOOL     gRgbReady = NO;    // true after startWriting completes
static dispatch_queue_t  gRgbQueue = NULL;  // serial queue; owns all writer access

extern "C" {

// codec: 0=H264, 1=HEVC
void SFRgbEncoder_Start(const char *mp4Path, int32_t width, int32_t height, int32_t codec) {
    gRgbReady = NO;
    gRgb = {};
    gRgbQueue = dispatch_queue_create("com.sensorflex.rgb_encode", DISPATCH_QUEUE_SERIAL);

    // Remove old file now (fast unlink; gRgbQueue doesn't exist yet).
    NSURL *url = [NSURL fileURLWithPath:@(mp4Path)];
    [[NSFileManager defaultManager] removeItemAtURL:url error:nil];

    // Capture params for the async block.
    NSString *pathStr   = [NSString stringWithUTF8String:mp4Path];
    NSString *codecType = (codec == 0) ? AVVideoCodecTypeH264 : AVVideoCodecTypeHEVC;
    int32_t   w = width, h = height;

    dispatch_async(gRgbQueue, ^{
        NSError *err  = nil;
        NSURL   *aUrl = [NSURL fileURLWithPath:pathStr];
        gRgb.writer   = [AVAssetWriter assetWriterWithURL:aUrl fileType:AVFileTypeMPEG4 error:&err];
        if (!gRgb.writer) { NSLog(@"[SF] SFRgbEncoder_Start: writer creation failed: %@", err); return; }

        NSDictionary *settings = @{
            AVVideoCodecKey:  codecType,
            AVVideoWidthKey:  @(w),
            AVVideoHeightKey: @(h),
            AVVideoCompressionPropertiesKey: @{
                AVVideoExpectedSourceFrameRateKey: @60,
                AVVideoAllowFrameReorderingKey:    @NO,
            },
        };
        gRgb.input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo
                                                        outputSettings:settings];
        gRgb.input.expectsMediaDataInRealTime = YES;

        NSDictionary *pbAttrs = @{
            (NSString *)kCVPixelBufferPixelFormatTypeKey:     @(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange),
            (NSString *)kCVPixelBufferWidthKey:               @(w),
            (NSString *)kCVPixelBufferHeightKey:              @(h),
            (NSString *)kCVPixelBufferIOSurfacePropertiesKey: @{},
        };
        gRgb.adaptor = [AVAssetWriterInputPixelBufferAdaptor
            assetWriterInputPixelBufferAdaptorWithAssetWriterInput:gRgb.input
                                          sourcePixelBufferAttributes:pbAttrs];

        [gRgb.writer addInput:gRgb.input];
        if (![gRgb.writer startWriting]) {
            NSLog(@"[SF] SFRgbEncoder_Start: startWriting failed: %@", gRgb.writer.error);
            gRgb = {};
            return;
        }
        [gRgb.writer startSessionAtSourceTime:kCMTimeZero];
        gRgbReady = YES;  // written last; main thread reads this as the readiness gate
        NSLog(@"[SF] SFRgbEncoder: ready (background)");
    });
}

// Returns 1 on success, 0 if encoder is not yet ready or frame was dropped.
// Pixel copy happens here on the main thread (~2 ms); VideoToolbox encode is
// dispatched to gRgbQueue so it never blocks the main thread.
int32_t SFRgbEncoder_AppendFrame(void *pY,    int32_t strideY,
                                  void *pCbCr, int32_t strideCbCr,
                                  int32_t width, int32_t height,
                                  int64_t timestampNs) {
    if (!gRgbReady) return 0;
    if (!gRgb.adaptor.pixelBufferPool) return 0;

    CVPixelBufferRef pixBuf = NULL;
    if (CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault,
                                           gRgb.adaptor.pixelBufferPool,
                                           &pixBuf) != kCVReturnSuccess || !pixBuf) return 0;

    CVPixelBufferLockBaseAddress(pixBuf, 0);

    uint8_t *yDst    = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pixBuf, 0);
    size_t   yDstRow = CVPixelBufferGetBytesPerRowOfPlane(pixBuf, 0);
    uint8_t *ySrc    = (uint8_t *)pY;
    for (int32_t row = 0; row < height; row++)
        memcpy(yDst + row * yDstRow, ySrc + row * (size_t)strideY, (size_t)width);

    uint8_t *uvDst    = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pixBuf, 1);
    size_t   uvDstRow = CVPixelBufferGetBytesPerRowOfPlane(pixBuf, 1);
    uint8_t *uvSrc    = (uint8_t *)pCbCr;
    for (int32_t row = 0; row < height / 2; row++)
        memcpy(uvDst + row * uvDstRow, uvSrc + row * (size_t)strideCbCr, (size_t)width);

    CVPixelBufferUnlockBaseAddress(pixBuf, 0);

    // Hand off to the encode queue. CFRetain keeps pixBuf alive until the block runs.
    CMTime time = CMTimeMake(timestampNs, 1000000000LL);
    CFRetain(pixBuf);
    dispatch_async(gRgbQueue, ^{
        if (gRgb.input.isReadyForMoreMediaData)
            [gRgb.adaptor appendPixelBuffer:pixBuf withPresentationTime:time];
        CVPixelBufferRelease(pixBuf);
    });
    CVPixelBufferRelease(pixBuf);  // release our ref; block holds its own via CFRetain
    return 1;
}

void SFRgbEncoder_Finish(SFEncoderDoneCallback callback) {
    if (!gRgbQueue) { if (callback) callback(0); return; }
    // Drain pending encodes, then mark finished — all on gRgbQueue to preserve order.
    dispatch_async(gRgbQueue, ^{
        if (!gRgb.input) { if (callback) callback(0); return; }
        [gRgb.input markAsFinished];
        [gRgb.writer finishWritingWithCompletionHandler:^{
            int ok = (gRgb.writer.status == AVAssetWriterStatusCompleted) ? 1 : 0;
            if (!ok) NSLog(@"[SF] SFRgbEncoder_Finish failed: %@", gRgb.writer.error);
            gRgb      = {};
            gRgbReady = NO;
            gRgbQueue = NULL;
            if (callback) callback(ok);
        }];
    });
}

// ─── Depth LZ4 writer ─────────────────────────────────────────────────────────
//
// Each depth frame: copy float32 plane → COMPRESSION_LZ4_RAW → append to depth.bin.
// Compressed byte count (int32 LE) appended to depth_sizes.bin.
// Both writes happen on a serial GCD queue (background thread).

static dispatch_queue_t    gDepthQueue    = NULL;
static FILE               *gDepthBinFile  = NULL;
static FILE               *gDepthSizFile  = NULL;
static compression_algorithm gDepthBinAlgo = COMPRESSION_LZ4_RAW;

// algo: 0=LZ4_RAW, 1=ZSTD
void SFDepthLz4_Start(const char *binPath, const char *sizesPath,
                       int32_t width, int32_t height, int32_t algo) {
    gDepthBinAlgo = (algo == 1) ? COMPRESSION_ZSTD : COMPRESSION_LZ4_RAW;
    remove(binPath);
    remove(sizesPath);
    gDepthBinFile = fopen(binPath,   "wb");
    gDepthSizFile = fopen(sizesPath, "wb");
    if (!gDepthBinFile || !gDepthSizFile) {
        NSLog(@"[SF] SFDepthLz4_Start: failed to open output files");
        return;
    }
    gDepthQueue = dispatch_queue_create("com.sensorflex.depth_lz4",
                                         DISPATCH_QUEUE_SERIAL);
}

// Copy depth plane, enqueue LZ4 compression + write to background queue.
// Returns 1 if successfully enqueued, 0 on failure (frame dropped).
int32_t SFDepthLz4_AppendFrame(void *pF32, int32_t stride,
                                 int32_t width, int32_t height) {
    if (!gDepthQueue) return 0;

    int32_t srcSize = width * height * 4;
    void *buf = malloc(srcSize);
    if (!buf) return 0;

    // Row-by-row copy to de-stride the plane (ARKit stride may exceed width*4).
    for (int32_t row = 0; row < height; row++)
        memcpy((char*)buf + row * width * 4,
               (char*)pF32 + row * stride,
               width * 4);

    dispatch_async(gDepthQueue, ^{
        // LZ4 worst-case: srcSize + srcSize/255 + 16; use +1/8 + 256 for safety.
        size_t maxDst = (size_t)srcSize + (srcSize >> 3) + 256;
        void  *dst    = malloc(maxDst);
        size_t compLen = 0;
        if (dst)
            compLen = compression_encode_buffer((uint8_t *)dst, maxDst,
                                                (const uint8_t *)buf, srcSize,
                                                NULL, gDepthBinAlgo);
        free(buf);

        // Always write a size entry (0 on failure) so depth_sizes.bin stays aligned
        // with frames that reported HasDepth = true.
        if (gDepthBinFile && gDepthSizFile) {
            if (compLen > 0) fwrite(dst, 1, compLen, gDepthBinFile);
            int32_t len32 = (int32_t)compLen;
            fwrite(&len32, sizeof(int32_t), 1, gDepthSizFile);
        }
        if (dst) free(dst);
    });
    return 1;
}

// Drain the serial queue, close both files, then invoke callback.
void SFDepthLz4_Finish(SFEncoderDoneCallback callback) {
    if (!gDepthQueue) { if (callback) callback(1); return; }
    dispatch_queue_t q = gDepthQueue;
    gDepthQueue = NULL;   // prevent new appends from enqueueing
    dispatch_async(q, ^{
        if (gDepthBinFile) { fclose(gDepthBinFile); gDepthBinFile = NULL; }
        if (gDepthSizFile) { fclose(gDepthSizFile); gDepthSizFile = NULL; }
        if (callback) callback(1);
    });
}

// ─── Depth Video encoder ──────────────────────────────────────────────────────
//
// Same background-queue pattern as the RGB encoder — all AVAssetWriter/VideoToolbox
// work runs on gDepthVideoQueue. The main thread fills a CVPixelBuffer (~1 ms),
// then dispatches the encode, preventing the ~1.4 s freeze from HEVC warm-up.

static SFEncoderSession  gDepthVideo         = {};
static volatile BOOL     gDepthVideoReady    = NO;
static dispatch_queue_t  gDepthVideoQueue    = NULL;
static float             gDepthMaxMeters     = 10.0f;
static BOOL              gDepthVideoIsHEVC   = NO;

// codec: 0=H264, 1=HEVC
void SFDepthVideo_Start(const char *mp4Path, int32_t width, int32_t height,
                         int32_t codec, float maxDepthMeters) {
    gDepthVideoReady  = NO;
    gDepthVideo       = {};
    gDepthMaxMeters   = maxDepthMeters > 0.0f ? maxDepthMeters : 10.0f;
    gDepthVideoIsHEVC = (codec == 1);
    gDepthVideoQueue  = dispatch_queue_create("com.sensorflex.depth_encode", DISPATCH_QUEUE_SERIAL);

    NSURL *url = [NSURL fileURLWithPath:@(mp4Path)];
    [[NSFileManager defaultManager] removeItemAtURL:url error:nil];

    NSString *pathStr   = [NSString stringWithUTF8String:mp4Path];
    BOOL      isHEVC    = (codec == 1);
    int32_t   w = width, h = height;
    float     maxD = gDepthMaxMeters;

    dispatch_async(gDepthVideoQueue, ^{
        OSType    pixelFormat = isHEVC ? kCVPixelFormatType_OneComponent16Half
                                        : kCVPixelFormatType_420YpCbCr8BiPlanarFullRange;
        NSString *codecType   = isHEVC ? AVVideoCodecTypeHEVC : AVVideoCodecTypeH264;

        NSError *err    = nil;
        NSURL   *aUrl   = [NSURL fileURLWithPath:pathStr];
        gDepthVideo.writer = [AVAssetWriter assetWriterWithURL:aUrl fileType:AVFileTypeMPEG4 error:&err];
        if (!gDepthVideo.writer) {
            NSLog(@"[SF] SFDepthVideo_Start: writer creation failed: %@", err);
            return;
        }

        NSDictionary *settings = @{
            AVVideoCodecKey:  codecType,
            AVVideoWidthKey:  @(w),
            AVVideoHeightKey: @(h),
            AVVideoCompressionPropertiesKey: @{
                AVVideoExpectedSourceFrameRateKey: @60,
                AVVideoAllowFrameReorderingKey:    @NO,
            },
        };
        gDepthVideo.input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo
                                                               outputSettings:settings];
        gDepthVideo.input.expectsMediaDataInRealTime = YES;

        NSDictionary *pbAttrs = @{
            (NSString *)kCVPixelBufferPixelFormatTypeKey:     @(pixelFormat),
            (NSString *)kCVPixelBufferWidthKey:               @(w),
            (NSString *)kCVPixelBufferHeightKey:              @(h),
            (NSString *)kCVPixelBufferIOSurfacePropertiesKey: @{},
        };
        gDepthVideo.adaptor = [AVAssetWriterInputPixelBufferAdaptor
            assetWriterInputPixelBufferAdaptorWithAssetWriterInput:gDepthVideo.input
                                          sourcePixelBufferAttributes:pbAttrs];

        [gDepthVideo.writer addInput:gDepthVideo.input];
        if (![gDepthVideo.writer startWriting]) {
            NSLog(@"[SF] SFDepthVideo_Start: startWriting failed: %@", gDepthVideo.writer.error);
            gDepthVideo = {};
            return;
        }
        [gDepthVideo.writer startSessionAtSourceTime:kCMTimeZero];
        gDepthVideoReady = YES;
        NSLog(@"[SF] SFDepthVideo_Start: ready (background) codec=%@ %dx%d maxDepth=%.1f",
              codecType, (int)w, (int)h, maxD);
    });
}

// Fills a CVPixelBuffer on the main thread (~1 ms), then dispatches the encode.
int32_t SFDepthVideo_AppendFrame(void *pF32, int32_t stride,
                                  int32_t width, int32_t height,
                                  int64_t timestampNs) {
    if (!gDepthVideoReady) return 0;
    if (!gDepthVideo.adaptor.pixelBufferPool) return 0;

    CVPixelBufferRef pixBuf = NULL;
    if (CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault,
                                           gDepthVideo.adaptor.pixelBufferPool,
                                           &pixBuf) != kCVReturnSuccess || !pixBuf) return 0;

    CVPixelBufferLockBaseAddress(pixBuf, 0);

    if (gDepthVideoIsHEVC) {
        uint8_t *dst    = (uint8_t *)CVPixelBufferGetBaseAddress(pixBuf);
        size_t   dstRow = CVPixelBufferGetBytesPerRow(pixBuf);
        uint8_t *src    = (uint8_t *)pF32;
        for (int32_t row = 0; row < height; row++) {
            vImage_Buffer srcBuf = { src + row * (size_t)stride, 1, (vImagePixelCount)width, (size_t)width * 4 };
            vImage_Buffer dstBuf = { dst + row * dstRow,         1, (vImagePixelCount)width, dstRow };
            vImageConvert_PlanarFtoPlanar16F(&srcBuf, &dstBuf, 0);
        }
    } else {
        uint8_t *yDst  = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pixBuf, 0);
        size_t   yRow  = CVPixelBufferGetBytesPerRowOfPlane(pixBuf, 0);
        uint8_t *uvDst = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(pixBuf, 1);
        size_t   uvRow = CVPixelBufferGetBytesPerRowOfPlane(pixBuf, 1);
        float   *src   = (float *)pF32;
        float    scale = 255.0f / gDepthMaxMeters;
        for (int32_t row = 0; row < height; row++) {
            float   *srcRow = (float *)((uint8_t *)src + row * (size_t)stride);
            uint8_t *dstRow = yDst + row * yRow;
            for (int32_t col = 0; col < width; col++) {
                float d = srcRow[col] * scale;
                dstRow[col] = (uint8_t)(d < 0.0f ? 0 : d > 255.0f ? 255 : d);
            }
        }
        for (int32_t row = 0; row < height / 2; row++)
            memset(uvDst + row * uvRow, 128, (size_t)width);
    }

    CVPixelBufferUnlockBaseAddress(pixBuf, 0);

    CMTime time = CMTimeMake(timestampNs, 1000000000LL);
    CFRetain(pixBuf);
    dispatch_async(gDepthVideoQueue, ^{
        if (gDepthVideo.input.isReadyForMoreMediaData)
            [gDepthVideo.adaptor appendPixelBuffer:pixBuf withPresentationTime:time];
        CVPixelBufferRelease(pixBuf);
    });
    CVPixelBufferRelease(pixBuf);
    return 1;
}

void SFDepthVideo_Finish(SFEncoderDoneCallback callback) {
    if (!gDepthVideoQueue) { if (callback) callback(0); return; }
    dispatch_async(gDepthVideoQueue, ^{
        if (!gDepthVideo.input) { if (callback) callback(0); return; }
        [gDepthVideo.input markAsFinished];
        [gDepthVideo.writer finishWritingWithCompletionHandler:^{
            int ok = (gDepthVideo.writer.status == AVAssetWriterStatusCompleted) ? 1 : 0;
            if (!ok) NSLog(@"[SF] SFDepthVideo_Finish failed: %@", gDepthVideo.writer.error);
            gDepthVideo      = {};
            gDepthVideoReady = NO;
            gDepthVideoQueue = NULL;
            if (callback) callback(ok);
        }];
    });
}

// ─── Block decoder (used by player via P/Invoke) ──────────────────────────────
//
// algo: 0=LZ4_RAW, 1=ZSTD — mirrors the algo index used by SFDepthLz4_Start.
// Returns bytes written to dst, or 0 on failure.

size_t SFDecodeBlock(const uint8_t *src, size_t srcLen,
                     uint8_t *dst, size_t dstLen,
                     int32_t algo) {
    compression_algorithm a = (algo == 1) ? COMPRESSION_ZSTD : COMPRESSION_LZ4_RAW;
    return compression_decode_buffer(dst, dstLen, src, srcLen, NULL, a);
}

// ─── Readiness queries ────────────────────────────────────────────────────────
//
// Called from C# to poll whether the async encoder setup completed.

int32_t SFRgbEncoderIsReady(void)       { return gRgbReady       ? 1 : 0; }
int32_t SFDepthVideoEncoderIsReady(void) { return gDepthVideoReady ? 1 : 0; }

// ─── One-time VideoToolbox prewarm ───────────────────────────────────────────
//
// Encodes one black 64×64 HEVC frame (both YCbCr and OneComponent16Half) on a
// background queue so that VideoToolbox is fully initialised before the first
// real recording starts. Call once from Awake.

void SFPrewarmEncoders(void) {
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        dispatch_queue_t q = dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0);
        dispatch_async(q, ^{
            NSArray *formats = @[ @(kCVPixelFormatType_420YpCbCr8BiPlanarFullRange),
                                  @(kCVPixelFormatType_OneComponent16Half) ];
            for (NSNumber *fmt in formats) {
                OSType pixFmt = (OSType)fmt.unsignedIntValue;
                NSString *tmp = [NSTemporaryDirectory()
                    stringByAppendingPathComponent:
                        [NSString stringWithFormat:@"sf_vt_warm_%u.mp4", (unsigned)pixFmt]];
                NSURL *url = [NSURL fileURLWithPath:tmp];
                [[NSFileManager defaultManager] removeItemAtURL:url error:nil];

                NSError *err = nil;
                AVAssetWriter *writer = [AVAssetWriter assetWriterWithURL:url
                                                                 fileType:AVFileTypeMPEG4
                                                                    error:&err];
                if (!writer) continue;

                NSDictionary *vs = @{
                    AVVideoCodecKey:  AVVideoCodecTypeHEVC,
                    AVVideoWidthKey:  @64,
                    AVVideoHeightKey: @64,
                };
                AVAssetWriterInput *input = [AVAssetWriterInput
                    assetWriterInputWithMediaType:AVMediaTypeVideo outputSettings:vs];
                input.expectsMediaDataInRealTime = YES;

                NSDictionary *pbAttrs = @{
                    (NSString *)kCVPixelBufferPixelFormatTypeKey: @(pixFmt),
                    (NSString *)kCVPixelBufferWidthKey:           @64,
                    (NSString *)kCVPixelBufferHeightKey:          @64,
                };
                AVAssetWriterInputPixelBufferAdaptor *adaptor = [
                    AVAssetWriterInputPixelBufferAdaptor
                    assetWriterInputPixelBufferAdaptorWithAssetWriterInput:input
                    sourcePixelBufferAttributes:pbAttrs];

                [writer addInput:input];
                if (![writer startWriting]) continue;
                [writer startSessionAtSourceTime:kCMTimeZero];

                CVPixelBufferRef pb = NULL;
                if (CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault,
                                                       adaptor.pixelBufferPool, &pb) == 0 && pb) {
                    CVPixelBufferLockBaseAddress(pb, 0);
                    size_t n = CVPixelBufferGetDataSize(pb);
                    memset(CVPixelBufferGetBaseAddress(pb), 0, n);
                    CVPixelBufferUnlockBaseAddress(pb, 0);
                    [adaptor appendPixelBuffer:pb withPresentationTime:kCMTimeZero];
                    CVPixelBufferRelease(pb);
                }

                [input markAsFinished];
                [writer finishWritingWithCompletionHandler:^{
                    [[NSFileManager defaultManager] removeItemAtURL:url error:nil];
                }];
            }
            NSLog(@"[SF] VideoToolbox prewarm complete");
        });
    });
}

// ─── Capability check ──────────────────────────────────────────────────────────

void SFLogCapabilities(int32_t rgbCodec, int32_t depthCodec) {
    NSString *rgbName   = (rgbCodec   == 0) ? @"H264"  : @"HEVC";
    NSString *depthName = (depthCodec == 0) ? @"LZ4"   : (depthCodec == 1 ? @"HEVC" : @"H264");

    NSOperatingSystemVersion ios11 = { .majorVersion = 11, .minorVersion = 0, .patchVersion = 0 };
    BOOL hevcAvailable = [[NSProcessInfo processInfo] isOperatingSystemAtLeastVersion:ios11];

    if ((rgbCodec == 1 || depthCodec == 1) && !hevcAvailable) {
        NSLog(@"[SF] WARNING: HEVC requested but iOS < 11 detected. HEVC may not be available.");
    }

    NSLog(@"[SF] SFLogCapabilities: RGB codec=%@ Depth codec=%@ HEVC available=%@",
          rgbName, depthName, hevcAvailable ? @"YES" : @"NO");
}

} // extern "C"
