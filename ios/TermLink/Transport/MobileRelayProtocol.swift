// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Exact V1/V2 server contracts. iOS keeps its own device identity while using
/// the existing mobile "android" wire role for released-host compatibility.
enum MobileRelayProtocol {
    enum Channel { case realtime, files }
    static func webSocketRequest(_ signed: URLRequest, channel: Channel) throws -> URLRequest {
        guard let url = signed.url, url.scheme == "https",
              var components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              components.host != nil, components.user == nil, components.password == nil else { throw ClientError.invalidURL }
        components.scheme = "wss"
        guard let socketURL = components.url else { throw ClientError.invalidURL }
        var request = signed
        request.url = socketURL
        request.setValue(channel == .files ? "termlink.file-transfer.v2" : "termlink.realtime.v1", forHTTPHeaderField: "Sec-WebSocket-Protocol")
        return request
    }
}
