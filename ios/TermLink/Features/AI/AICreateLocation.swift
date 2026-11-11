// SPDX-License-Identifier: GPL-3.0-only
import Foundation

struct AICreateLocation: Identifiable, Equatable {
    let pcID: UUID
    let directory: String
    var id: String { pcID.uuidString + "\n" + directory }

    init(pcID: UUID, directory: String) throws {
        guard !directory.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              directory.utf8.count <= 4096, !directory.contains("\0") else {
            throw ClientError.invalidResponse("AI working directory")
        }
        self.pcID = pcID; self.directory = directory
    }

    static func eligible(_ pc: PC) -> Bool {
        pc.online && pc.routing && pc.capabilities.contains("ai_agent_v1")
    }

    func validate(in computers: [PC]) throws {
        guard computers.contains(where: { $0.id == pcID && Self.eligible($0) }) else {
            throw ClientError.unavailable("所选电脑当前无法创建 AI 会话，请重新确认电脑状态。")
        }
    }
}
