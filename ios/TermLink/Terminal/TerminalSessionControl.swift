// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Control of a catalog session that has no open terminal view still requires
/// an authenticated host subscription. No terminal bytes are ACKed by this path.
enum TerminalSessionControl {
    static func subscription(requestID: UUID, exchangeKey: Data) throws -> ProtoMessage {
        guard exchangeKey.count == 32 else { throw ClientError.authentication }
        var payload = ProtoMessage()
        payload.set(1, UInt64(0)); payload.set(2, exchangeKey); payload.set(4, requestID.bytes)
        for capability in ["terminal_attach_v1", "terminal_state_2_21_v2"] {
            payload.add(3, Data(capability.utf8))
        }
        return payload
    }

    @MainActor static func terminateUnopened(
        reset: () async throws -> Void,
        authorize: () async throws -> Void,
        attach: () async throws -> ProtoMessage,
        terminate: () async throws -> ProtoMessage,
        unsubscribe: () async -> Void
    ) async throws {
        // The same WebSocket can retain a cumulative ACK from a closed view.
        // Reset its server subscription before attaching from a fresh zero ACK.
        try await reset()
        try await authorize()
        do {
            try Task.checkCancellation()
            let attached = try await attach()
            guard try attached.text(3).isEmpty,
                  try attached.text(2) == "terminal_state_2_21_v2" else {
                throw ClientError.unavailable("主机未确认会话订阅，未发送结束请求。")
            }
            try Task.checkCancellation()
            let reply = try await terminate()
            guard reply.uint(2) == 1, try reply.text(3).isEmpty else {
                throw ClientError.unavailable("电脑尚未确认结束会话。")
            }
        } catch {
            await unsubscribe()
            throw error
        }
        await unsubscribe()
    }
}
