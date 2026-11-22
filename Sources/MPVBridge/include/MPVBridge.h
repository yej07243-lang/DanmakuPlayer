#import <AppKit/AppKit.h>
NS_ASSUME_NONNULL_BEGIN
@interface DPVideoView : NSOpenGLView
@property(nonatomic, readonly) NSString *engineError;
@property(nonatomic, readonly) NSString *version;
- (int)command:(NSArray<NSString *> *)arguments;
- (double)number:(NSString *)property;
- (NSString *)string:(NSString *)property;
- (NSString *)json:(NSString *)property;
- (int)setNumber:(double)value property:(NSString *)property;
- (int)setString:(NSString *)value property:(NSString *)property;
- (void)poll;
- (void)shutdown;
@end
NS_ASSUME_NONNULL_END
/// Headless, bounded thumbnail generation. Call from a background worker, never the UI thread.
FOUNDATION_EXPORT BOOL DPWriteThumbnail(NSString * _Nonnull path, NSString * _Nonnull destination);
