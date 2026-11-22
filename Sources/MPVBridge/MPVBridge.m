#import "MPVBridge.h"
#include <mpv/client.h>
#include <mpv/render_gl.h>
#import <OpenGL/gl3.h>
#include <dlfcn.h>

static void *get_proc(void *ctx, const char *name) { return dlsym(RTLD_DEFAULT, name); }
static id node_object(mpv_node *node) {
    switch (node->format) {
        case MPV_FORMAT_STRING: return node->u.string ? @(node->u.string) : @"";
        case MPV_FORMAT_FLAG: return @(node->u.flag != 0);
        case MPV_FORMAT_INT64: return @(node->u.int64);
        case MPV_FORMAT_DOUBLE: return isfinite(node->u.double_) ? @(node->u.double_) : @0;
        case MPV_FORMAT_NODE_ARRAY: {
            NSMutableArray *array = [NSMutableArray array];
            for (int i=0; i<node->u.list->num; i++) [array addObject:node_object(&node->u.list->values[i])];
            return array;
        }
        case MPV_FORMAT_NODE_MAP: {
            NSMutableDictionary *dict = [NSMutableDictionary dictionary];
            for (int i=0; i<node->u.list->num; i++) dict[@(node->u.list->keys[i])] = node_object(&node->u.list->values[i]);
            return dict;
        }
        default: return [NSNull null];
    }
}
@implementation DPVideoView {
    mpv_handle *_mpv;
    mpv_render_context *_renderer;
    NSTimer *_renderTimer;
    dispatch_queue_t _renderQueue;
    NSOpenGLContext *_drawingContext;
    BOOL _renderScheduled;
    BOOL _closing;
    BOOL _forceFrame;
    NSString *_engineError;
}
- (instancetype)initWithFrame:(NSRect)frame {
    NSOpenGLPixelFormatAttribute attributes[] = {NSOpenGLPFAOpenGLProfile, NSOpenGLProfileVersion3_2Core, NSOpenGLPFAAccelerated, NSOpenGLPFADoubleBuffer, NSOpenGLPFAColorSize, 24, NSOpenGLPFAAlphaSize, 8, 0};
    self = [super initWithFrame:frame pixelFormat:[[NSOpenGLPixelFormat alloc] initWithAttributes:attributes]];
    if (!self) return nil;
    _engineError = @""; self.wantsBestResolutionOpenGLSurface = YES;
    _mpv = mpv_create();
    if (!_mpv) { _engineError = @"无法创建 libmpv"; return self; }
    mpv_set_option_string(_mpv, "config", "no");
    if (getenv("DANMAKU_QA_MUTE")) mpv_set_option_string(_mpv, "volume", "0");
    mpv_set_option_string(_mpv, "terminal", "no");
    mpv_set_option_string(_mpv, "vo", "libmpv");
    mpv_set_option_string(_mpv, "hwdec", "auto-safe");
    mpv_set_option_string(_mpv, "keep-open", "yes");
    mpv_set_option_string(_mpv, "idle", "yes");
    mpv_set_option_string(_mpv, "ytdl", "no");
    mpv_set_option_string(_mpv, "input-default-bindings", "no");
    mpv_set_option_string(_mpv, "osc", "no");
    mpv_set_option_string(_mpv, "screenshot-format", "png");
    int status = mpv_initialize(_mpv);
    if (status < 0) { _engineError = @(mpv_error_string(status)); return self; }
    [self.openGLContext makeCurrentContext];
    _drawingContext = self.openGLContext;
    _renderQueue = dispatch_queue_create("app.danmaku.video-render", DISPATCH_QUEUE_SERIAL);
    GLint swap = 1; [self.openGLContext setValues:&swap forParameter:NSOpenGLContextParameterSwapInterval];
    mpv_opengl_init_params gl = {get_proc, NULL};
    mpv_render_param params[] = {{MPV_RENDER_PARAM_API_TYPE, MPV_RENDER_API_TYPE_OPENGL}, {MPV_RENDER_PARAM_OPENGL_INIT_PARAMS, &gl}, {MPV_RENDER_PARAM_INVALID, NULL}};
    status = mpv_render_context_create(&_renderer, _mpv, params);
    if (status < 0) _engineError = [NSString stringWithFormat:@"视频渲染初始化失败：%s", mpv_error_string(status)];
    __weak DPVideoView *weak = self;
    _renderTimer = [NSTimer timerWithTimeInterval:1.0/60 repeats:YES block:^(NSTimer *timer) {
        DPVideoView *view = weak;
        if (view && view.window.isVisible) [view enqueueRender:NO];
    }];
    [[NSRunLoop mainRunLoop] addTimer:_renderTimer forMode:NSRunLoopCommonModes];
    return self;
}
- (BOOL)isOpaque { return YES; }
- (void)enqueueRender:(BOOL)force {
    if (_closing || !_renderer || !_drawingContext) return;
    _forceFrame |= force;
    if (_renderScheduled) return;
    NSRect rect = [self convertRectToBacking:self.bounds];
    if (rect.size.width < 1 || rect.size.height < 1) return;
    _renderScheduled = YES;
    BOOL forceFrame = _forceFrame; _forceFrame = NO;
    __weak DPVideoView *weak = self;
    dispatch_async(_renderQueue, ^{
        DPVideoView *view = weak;
        if (!view) return;
        // mpv timing and vertical-sync waits must never block AppKit's drawing thread.
        NSOpenGLContext *context = view->_drawingContext;
        CGLLockContext(context.CGLContextObj);
        [context makeCurrentContext];
        uint64_t flags = mpv_render_context_update(view->_renderer);
        if (forceFrame || (flags & MPV_RENDER_UPDATE_FRAME)) {
            mpv_opengl_fbo fbo = {0, (int)rect.size.width, (int)rect.size.height, 0};
            int flip = 1;
            mpv_render_param params[] = {{MPV_RENDER_PARAM_OPENGL_FBO, &fbo}, {MPV_RENDER_PARAM_FLIP_Y, &flip}, {MPV_RENDER_PARAM_INVALID, NULL}};
            mpv_render_context_render(view->_renderer, params);
            [context flushBuffer];
            mpv_render_context_report_swap(view->_renderer);
        }
        [NSOpenGLContext clearCurrentContext];
        CGLUnlockContext(context.CGLContextObj);
        dispatch_async(dispatch_get_main_queue(), ^{ view->_renderScheduled = NO; });
    });
}
- (void)drawRect:(NSRect)dirtyRect { [self enqueueRender:YES]; }
- (void)reshape {
    [super reshape];
    if (_drawingContext) {
        CGLLockContext(_drawingContext.CGLContextObj);
        [_drawingContext update];
        CGLUnlockContext(_drawingContext.CGLContextObj);
    }
    [self enqueueRender:YES];
}
- (NSString *)engineError { return _engineError ?: @""; }
- (NSString *)version { return [self string:@"mpv-version"]; }
- (int)command:(NSArray<NSString *> *)arguments {
    if (!_mpv) return -1;
    const char **args = calloc(arguments.count + 1, sizeof(char *));
    for (NSUInteger i=0; i<arguments.count; i++) args[i] = arguments[i].UTF8String;
    int result = mpv_command(_mpv, args); free(args);
    if (result < 0) _engineError = @(mpv_error_string(result));
    return result;
}
- (double)number:(NSString *)property { double value = 0; if (_mpv) mpv_get_property(_mpv, property.UTF8String, MPV_FORMAT_DOUBLE, &value); return isfinite(value) ? value : 0; }
- (NSString *)string:(NSString *)property {
    if (!_mpv) return @"";
    char *value = mpv_get_property_string(_mpv, property.UTF8String);
    if (!value) return @"";
    NSString *text = @(value); mpv_free(value); return text;
}
- (NSString *)json:(NSString *)property {
    mpv_node node;
    if (!_mpv || mpv_get_property(_mpv, property.UTF8String, MPV_FORMAT_NODE, &node) < 0) return @"null";
    id obj = node_object(&node);
    NSData *data = [NSJSONSerialization dataWithJSONObject:obj options:NSJSONWritingFragmentsAllowed error:nil];
    mpv_free_node_contents(&node);
    return data ? [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] : @"null";
}
- (int)setNumber:(double)value property:(NSString *)property { return _mpv ? mpv_set_property(_mpv, property.UTF8String, MPV_FORMAT_DOUBLE, &value) : -1; }
- (int)setString:(NSString *)value property:(NSString *)property { return _mpv ? mpv_set_property_string(_mpv, property.UTF8String, value.UTF8String) : -1; }
- (void)poll {
    if (!_mpv) return;
    while (YES) {
        mpv_event *event = mpv_wait_event(_mpv, 0);
        if (event->event_id == MPV_EVENT_NONE) break;
        if (event->event_id == MPV_EVENT_END_FILE) {
            mpv_event_end_file *end = event->data;
            if (end->reason == MPV_END_FILE_REASON_ERROR) _engineError = [NSString stringWithFormat:@"无法播放此文件：%s", mpv_error_string(end->error)];
        }
        if (event->event_id == MPV_EVENT_FILE_LOADED) _engineError = @"";
    }
}
- (void)shutdown {
    _closing = YES;
    [_renderTimer invalidate]; _renderTimer = nil;
    if (_renderQueue) dispatch_sync(_renderQueue, ^{
        [self->_drawingContext makeCurrentContext];
        if (self->_renderer) { mpv_render_context_free(self->_renderer); self->_renderer = NULL; }
        [NSOpenGLContext clearCurrentContext];
    });
    if (_mpv) { mpv_terminate_destroy(_mpv); _mpv = NULL; }
}
- (void)dealloc { [self shutdown]; }
@end

BOOL DPWriteThumbnail(NSString *path, NSString *destination) {
    @autoreleasepool {
        NSFileManager *files = NSFileManager.defaultManager;
        if ([files fileExistsAtPath:destination]) return YES;
        NSString *folder = [NSTemporaryDirectory() stringByAppendingPathComponent:[@"danmaku-thumb-" stringByAppendingString:NSUUID.UUID.UUIDString]];
        if (![files createDirectoryAtPath:folder withIntermediateDirectories:YES attributes:nil error:nil]) return NO;
        mpv_handle *handle = mpv_create();
        if (!handle) { [files removeItemAtPath:folder error:nil]; return NO; }
        mpv_set_option_string(handle, "config", "no");
        mpv_set_option_string(handle, "terminal", "no");
        mpv_set_option_string(handle, "load-scripts", "no");
        mpv_set_option_string(handle, "ytdl", "no");
        mpv_set_option_string(handle, "vo", "image");
        mpv_set_option_string(handle, "vo-image-format", "jpg");
        mpv_set_option_string(handle, "vo-image-outdir", folder.UTF8String);
        mpv_set_option_string(handle, "audio", "no");
        mpv_set_option_string(handle, "start", "5%");
        mpv_set_option_string(handle, "frames", "1");
        mpv_set_option_string(handle, "vf", "scale=480:-2");
        if (mpv_initialize(handle) >= 0) {
            const char *args[] = {"loadfile", path.UTF8String, NULL};
            if (mpv_command(handle, args) >= 0) {
                NSTimeInterval deadline = NSProcessInfo.processInfo.systemUptime + 4;
                while (NSProcessInfo.processInfo.systemUptime < deadline) {
                    mpv_event *event = mpv_wait_event(handle, 0.05);
                    if (event->event_id == MPV_EVENT_END_FILE || event->event_id == MPV_EVENT_SHUTDOWN) break;
                }
            }
        }
        mpv_terminate_destroy(handle);
        NSString *image = nil;
        for (NSString *name in [files contentsOfDirectoryAtPath:folder error:nil]) {
            if ([name.pathExtension.lowercaseString isEqualToString:@"jpg"]) { image = [folder stringByAppendingPathComponent:name]; break; }
        }
        BOOL success = image && [files moveItemAtPath:image toPath:destination error:nil];
        [files removeItemAtPath:folder error:nil];
        return success;
    }
}
