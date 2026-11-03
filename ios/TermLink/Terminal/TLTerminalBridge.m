// SPDX-License-Identifier: GPL-3.0-only
#import "TLTerminalBridge.h"
#include <string.h>
#if __has_include(<TermLinkTerminalCore/termlink_terminal.h>)
#include <TermLinkTerminalCore/termlink_terminal.h>
#define TL_CORE_AVAILABLE 1
#else
#define TL_CORE_AVAILABLE 0
#endif
#if !TL_CORE_AVAILABLE && !DEBUG
#error A Release build requires the verified TermLinkTerminalCore XCFramework.
#endif

static BOOL TLCheck(uint32_t status, NSError **error) {
    if (status == 0) return YES;
    if (error) *error = [NSError errorWithDomain:@"dev.termlink.terminal" code:status userInfo:@{NSLocalizedDescriptionKey: status == 1000 ? @"缺少通过校验的 iOS terminal-core XCFramework，终端尚不可用。" : [NSString stringWithFormat:@"Terminal core rejected operation (%u).", status]}];
    return NO;
}
@implementation TLTerminalBridge {
#if TL_CORE_AVAILABLE
    TlTerminal *_terminal;
    TlTerminalHistoryCaptureV1 _history;
#endif
    uint32_t _historyAvailableRows;
}
+ (BOOL)available {
#if TL_CORE_AVAILABLE
    return tl_terminal_abi_version() == TL_TERMINAL_ABI_VERSION;
#else
    return NO;
#endif
}
- (instancetype)initWithColumns:(uint32_t)columns rows:(uint32_t)rows sequence:(uint64_t)sequence error:(NSError **)error {
    self = [super init]; if (!self) return nil;
#if TL_CORE_AVAILABLE
    TlTerminalConfigV4 config = {0}; config.abi_version = 1; config.struct_size = sizeof(config);
    config.columns = columns; config.rows = rows; config.initial_source_sequence = sequence;
    config.history_capacity_rows = 4000; config.output_profile = 0; config.width_profile = 1;
    if (!TLCheck(tl_terminal_create_v4(&config, &_terminal), error)) return nil;
    return self;
#else
    TLCheck(1000,error); return nil;
#endif
}
- (BOOL)applyData:(NSData *)data kind:(uint32_t)kind sequence:(uint64_t)sequence columns:(uint32_t)columns rows:(uint32_t)rows error:(NSError **)error {
#if TL_CORE_AVAILABLE
    TlTerminalEvent event = {0}; event.abi_version=1; event.struct_size=sizeof(event); event.source_sequence=sequence;
    event.kind=kind; event.data=data.length?data.bytes:NULL; event.data_len=data.length; event.columns=columns; event.rows=rows;
    return TLCheck(tl_terminal_apply_event(_terminal,&event),error);
#else
    return TLCheck(1000,error);
#endif
}
- (BOOL)importSnapshot:(NSData *)snapshot error:(NSError **)error {
#if TL_CORE_AVAILABLE
    TlTerminal *next=NULL; if (!TLCheck(tl_terminal_import_snapshot(snapshot.bytes,snapshot.length,&next),error)) return NO;
    [self endHistory]; tl_terminal_destroy(_terminal); _terminal=next; return YES;
#else
    return TLCheck(1000,error);
#endif
}
- (NSData *)renderFrame:(NSError **)error {
#if TL_CORE_AVAILABLE
    TlTerminalCheckpoint *checkpoint=NULL;
    if (!TLCheck(tl_terminal_checkpoint_begin(_terminal,&checkpoint),error)) return nil;
    TlTerminalBuffer frame={0}; frame.abi_version=1; frame.struct_size=sizeof(frame);
    uint32_t status=tl_terminal_checkpoint_export_render_frame_v4(checkpoint,32*1024*1024,&frame);
    tl_terminal_checkpoint_destroy(checkpoint);
    if (!TLCheck(status,error)) return nil;
    NSData *result=[NSData dataWithBytes:frame.data length:(NSUInteger)frame.len];
    tl_terminal_buffer_free(&frame); return result;
#else
    TLCheck(1000,error); return nil;
#endif
}
- (NSData *)encodeKey:(uint32_t)key scalar:(uint32_t)scalar modifiers:(uint32_t)modifiers paste:(NSData *)paste error:(NSError **)error {
#if TL_CORE_AVAILABLE
    TlTerminalInputV1 input={0}; input.abi_version=1; input.struct_size=sizeof(input); input.kind=paste?TL_INPUT_KIND_PASTE:TL_INPUT_KIND_KEY;
    input.key=paste?0:key; input.character=paste?0:scalar; input.modifiers=paste?0:modifiers; input.data=paste.length?paste.bytes:NULL; input.data_len=paste.length;
    TlTerminalBuffer result={0};result.abi_version=1;result.struct_size=sizeof(result);
    if (!TLCheck(tl_terminal_encode_input_v1(_terminal,&input,&result),error)) return nil;
    NSData *data=[NSData dataWithBytes:result.data length:(NSUInteger)result.len];tl_terminal_buffer_free(&result);return data;
#else
    TLCheck(1000,error); return nil;
#endif
}
- (NSData *)exportSnapshot:(NSError **)error {
#if TL_CORE_AVAILABLE
    TlTerminalBuffer snapshot={0};snapshot.abi_version=1;snapshot.struct_size=sizeof(snapshot);
    TlTerminalCheckpoint *checkpoint=NULL;
    if (!TLCheck(tl_terminal_checkpoint_begin(_terminal,&checkpoint),error)) return nil;
    uint32_t status=tl_terminal_checkpoint_export_snapshot(checkpoint,&snapshot);
    tl_terminal_checkpoint_destroy(checkpoint);
    if (!TLCheck(status,error)) return nil;
    NSData *result=[NSData dataWithBytes:snapshot.data length:(NSUInteger)snapshot.len];tl_terminal_buffer_free(&snapshot);return result;
#else
    TLCheck(1000,error);return nil;
#endif
}
- (uint32_t)historyAvailableRows { return _historyAvailableRows; }
- (NSData *)beginHistory:(NSError **)error {
#if TL_CORE_AVAILABLE
    [self endHistory];
    _history.abi_version=TL_TERMINAL_ABI_VERSION; _history.struct_size=sizeof(_history);
    if (!TLCheck(tl_terminal_history_begin_v1(_terminal, &_history),error)) {
        memset(&_history,0,sizeof(_history)); return nil;
    }
    NSData *frame=[self renderHistoryFrameAtOffset:0 error:error];
    if (!frame) [self endHistory];
    return frame;
#else
    TLCheck(1000,error); return nil;
#endif
}
- (NSData *)renderHistoryFrameAtOffset:(uint32_t)offset error:(NSError **)error {
#if TL_CORE_AVAILABLE
    if (!_history.capture_id) { TLCheck(TL_STATUS_HISTORY_STALE_CAPTURE,error); return nil; }
    TlTerminalBuffer frame={0}; frame.abi_version=TL_TERMINAL_ABI_VERSION; frame.struct_size=sizeof(frame);
    uint32_t available=0; uint64_t generation=0;
    if (!TLCheck(tl_terminal_history_viewport_v1(_history.capture_id,_history.owner_id,_history.revision,
        offset,&frame,&available,&generation),error)) return nil;
    NSData *data=[NSData dataWithBytes:frame.data length:(NSUInteger)frame.len];
    tl_terminal_buffer_free(&frame); _historyAvailableRows=available; return data;
#else
    TLCheck(1000,error); return nil;
#endif
}
- (NSNumber *)historyOffsetForLine:(uint64_t)line cellOffset:(uint32_t)cellOffset error:(NSError **)error {
#if TL_CORE_AVAILABLE
    if (!_history.capture_id) { TLCheck(TL_STATUS_HISTORY_STALE_CAPTURE,error); return nil; }
    uint32_t offset=0;
    if (!TLCheck(tl_terminal_history_resolve_anchor_v1(_history.capture_id,_history.owner_id,
        _history.revision,line,cellOffset,&offset),error)) return nil;
    return @(offset);
#else
    TLCheck(1000,error); return nil;
#endif
}
- (void)endHistory {
#if TL_CORE_AVAILABLE
    if (_history.capture_id) tl_terminal_history_release_v1(_history.capture_id,_history.owner_id);
    memset(&_history,0,sizeof(_history));
#endif
    _historyAvailableRows=0;
}
- (NSData *)shellState:(NSError **)error {
#if TL_CORE_AVAILABLE
    TlTerminalBuffer buffer={0};buffer.abi_version=TL_TERMINAL_ABI_VERSION;buffer.struct_size=sizeof(buffer);
    if (!TLCheck(tl_terminal_get_shell_state_v1(_terminal,273696,&buffer),error)) return nil;
    NSData *data=[NSData dataWithBytes:buffer.data length:(NSUInteger)buffer.len];
    tl_terminal_buffer_free(&buffer);return data;
#else
    TLCheck(1000,error);return nil;
#endif
}
- (void)dealloc {
#if TL_CORE_AVAILABLE
    if (_history.capture_id) tl_terminal_history_release_v1(_history.capture_id,_history.owner_id);
    if (_terminal) tl_terminal_destroy(_terminal);
#endif
}
@end
