# iOS source port

- Follow the repository's binary-only terminal-core policy. Never copy the core header, Rust, JNI or CMake sources here. The Objective-C bridge includes the header from the verified future XCFramework module only.
- This port has not passed an Xcode build or device acceptance. Do not advertise Android feature parity or a working terminal while the iOS XCFramework is absent.
- Keep AI presentation in the shared, bundled `android/app/src/main/assets/ai-web` resources. Remote third-party pages must never receive the TermLink native bridge or application tokens.
- Agent additions must update `AgentBrand`, aliases, bundled image sets, source/license notices, `sync_agent_assets.py` and the bundle asset test in the same change. Unknown agents keep the generic fallback.
- Retain phone tabs and iPad sidebar/detail behavior. Do not integrate the deferred free-window tablet design without a new request.
- Signatures, provisioning profiles, tokens and `Local.xcconfig` stay out of Git. Core Release builds must pass the artifact lock check; missing capability must disable that feature explicitly.
