// SPDX-License-Identifier: GPL-3.0-only
// Public read-only APIs only. No permission requests, TCC database access,
// keychain item reads, screen capture, automation events or credential access.
#import <Foundation/Foundation.h>
#import <Security/Security.h>
#import <UserNotifications/UserNotifications.h>
#import <ApplicationServices/ApplicationServices.h>
#include <unistd.h>

__attribute__((visibility("default"))) int tl_macos_keychain_status(void) {
    SecKeychainRef keychain = NULL;
    OSStatus result = SecKeychainCopyDefault(&keychain);
    if (result != errSecSuccess || keychain == NULL) return -1;
    SecKeychainStatus status = 0;
    result = SecKeychainGetStatus(keychain, &status);
    CFRelease(keychain);
    return result == errSecSuccess ? ((status & kSecUnlockStateStatus) ? 1 : 0) : -1;
}

__attribute__((visibility("default"))) int tl_macos_notification_status(void) {
    @autoreleasepool {
        // Avoid currentNotificationCenter exceptions for unbundled test hosts.
        if (NSBundle.mainBundle.bundleIdentifier.length == 0 || NSThread.isMainThread) return -1;
        @try {
            dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
            __block NSInteger value = -1;
            [UNUserNotificationCenter.currentNotificationCenter getNotificationSettingsWithCompletionHandler:^(UNNotificationSettings *settings) {
                value = settings.authorizationStatus;
                dispatch_semaphore_signal(semaphore);
            }];
            if (dispatch_semaphore_wait(semaphore, dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC)) != 0) return -1;
            return (int)value;
        } @catch (__unused NSException *exception) { return -1; }
    }
}

__attribute__((visibility("default"))) int tl_macos_accessibility_status(void) {
    return AXIsProcessTrusted() ? 1 : 0;
}

__attribute__((visibility("default"))) int tl_macos_directory_access(const char *directory) {
    if (directory == NULL || directory[0] != '/') return -1;
    if (access(directory, F_OK) != 0) return -1;
    return access(directory, R_OK | W_OK | X_OK) == 0 ? 1 : 0;
}
