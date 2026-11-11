// SPDX-License-Identifier: GPL-3.0-only
import Foundation

/// Account operations stay in the service's existing browser flows. The app
/// passes only the destination URL; browser authentication remains independent.
enum AccountWebPage: CaseIterable {
    case registration, loginHelp, security, privacy

    func url(server: String) throws -> URL {
        let root = try APIClient.origin(server)
        var components = URLComponents(url: root, resolvingAgainstBaseURL: false)!
        switch self {
        case .registration: components.path = "/register"
        case .loginHelp: components.path = "/login"
        case .security: components.path = "/account/identity"
        case .privacy:
            components.path = "/account"
            components.queryItems = [.init(name: "section", value: "privacy")]
        }
        guard let url = components.url else { throw ClientError.invalidURL }
        return url
    }
}
