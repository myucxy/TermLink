# Portable AI validation

Run this executable through its generated apphost (`dotnet run --project pc-client/tests/PortableAiValidation/TermLink.PortableAI.Validation.csproj`), with the repository's pinned .NET/protoc settings and normal locked restore workflow. Its assembly name is the shared module's friend `TermLink.PortableAI.Validation`.

The default run starts only this test program as a simulated ZCode app-server. It tests native-only catalogs, retired URL rejection, authorization changes, operation receipts, controller leases, approvals/questions, desktop-auth refusal, stop/end and process cleanup. Simulated text and approval events do not demonstrate a working model or actual tool execution. Every worker is temporary; no real account configuration or service is used.

`--real-zcode <new-absolute-evidence-directory>` is a separate opt-in macOS check of the installed ZCode runtime. It runs under a test-generated `sandbox-exec` policy, denies access to the existing ZCode configuration and desktop data, prohibits IP networking, and uses separate configuration/session directories. It must never send a prompt or call a model/tool. Missing independent model configuration is reported as a boundary rather than bypassed. The new evidence directory is preserved with a sanitized report identifying exactly which native methods succeeded; without an explicit directory a unique temporary evidence directory is used and reported.

These tests do not cover authenticated Relay transport, phone UI, desktop account authorization, real model responses, or physical devices. Run them from a Mac local source copy, not the NAS workspace. The project does not install tools, modify user configuration, commit, or publish artifacts.
