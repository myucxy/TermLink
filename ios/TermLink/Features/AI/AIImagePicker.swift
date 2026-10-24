// SPDX-License-Identifier: GPL-3.0-only
import PhotosUI
import SwiftUI
import UniformTypeIdentifiers

struct AIPhotoFile: Transferable {
    let url: URL
    static var transferRepresentation: some TransferRepresentation {
        FileRepresentation(importedContentType: .image) { received in
            let size = try received.file.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? Int.max
            guard size > 0, size <= 20 * 1024 * 1024 else { throw ClientError.limitExceeded }
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let name = received.file.lastPathComponent
            guard !name.isEmpty, name != ".", name != ".." else { throw ClientError.invalidResponse("photo filename") }
            let target = directory.appendingPathComponent(name)
            try FileManager.default.copyItem(at: received.file, to: target)
            try FileManager.default.setAttributes([.protectionKey: FileProtectionType.complete], ofItemAtPath: target.path)
            return AIPhotoFile(url: target)
        }
    }
}

struct AICameraView: UIViewControllerRepresentable {
    var completion: (Result<URL, Error>) -> Void
    func makeCoordinator() -> Coordinator { Coordinator(completion: completion) }
    func makeUIViewController(context: Context) -> UIImagePickerController {
        let picker = UIImagePickerController(); picker.delegate = context.coordinator
        picker.sourceType = .camera; picker.mediaTypes = [UTType.image.identifier]
        return picker
    }
    func updateUIViewController(_ controller: UIImagePickerController, context: Context) {}
    final class Coordinator: NSObject, UIImagePickerControllerDelegate, UINavigationControllerDelegate {
        let completion: (Result<URL, Error>) -> Void
        init(completion: @escaping (Result<URL, Error>) -> Void) { self.completion = completion }
        func imagePickerControllerDidCancel(_ picker: UIImagePickerController) { completion(.failure(ClientError.cancelled)) }
        func imagePickerController(_ picker: UIImagePickerController, didFinishPickingMediaWithInfo info: [UIImagePickerController.InfoKey: Any]) {
            do {
                guard let image = info[.originalImage] as? UIImage, let bytes = image.jpegData(compressionQuality: 0.85), bytes.count <= 20 * 1024 * 1024 else { throw ClientError.limitExceeded }
                let target = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".jpg")
                try bytes.write(to: target, options: [.atomic, .completeFileProtection]); completion(.success(target))
            } catch { completion(.failure(error)) }
        }
    }
}
