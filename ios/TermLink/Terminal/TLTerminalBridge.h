// SPDX-License-Identifier: GPL-3.0-only
#import <Foundation/Foundation.h>
NS_ASSUME_NONNULL_BEGIN
@interface TLTerminalBridge : NSObject
@property(class, nonatomic, readonly) BOOL available;
- (nullable instancetype)initWithColumns:(uint32_t)columns rows:(uint32_t)rows sequence:(uint64_t)sequence error:(NSError **)error;
- (BOOL)applyData:(NSData *)data kind:(uint32_t)kind sequence:(uint64_t)sequence columns:(uint32_t)columns rows:(uint32_t)rows error:(NSError **)error NS_SWIFT_NAME(applyData(_:kind:sequence:columns:rows:));
- (BOOL)importSnapshot:(NSData *)snapshot error:(NSError **)error;
- (nullable NSData *)renderFrame:(NSError **)error;
- (nullable NSData *)exportSnapshot:(NSError **)error;
- (nullable NSData *)encodeKey:(uint32_t)key scalar:(uint32_t)scalar modifiers:(uint32_t)modifiers paste:(nullable NSData *)paste error:(NSError **)error;
// Immutable, bounded native history; later live events do not change this capture.
@property(nonatomic, readonly) uint32_t historyAvailableRows;
- (nullable NSData *)beginHistory:(NSError **)error;
- (nullable NSData *)renderHistoryFrameAtOffset:(uint32_t)offset error:(NSError **)error;
- (nullable NSNumber *)historyOffsetForLine:(uint64_t)line cellOffset:(uint32_t)cellOffset error:(NSError **)error;
- (void)endHistory;
- (nullable NSData *)shellState:(NSError **)error;
@end
NS_ASSUME_NONNULL_END
