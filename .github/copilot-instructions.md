# Repository Instructions

- The extension publisher is `kkamegawa`.
- The VSIX identifier is `Kkamegawa.CodexForVisualStudio`.
- Write source code, code comments, identifiers, and diagnostics in English.
- Use UTF-8 with BOM and CRLF for source files.
- Keep Codex protocol, security policy, and process management outside the Visual Studio process.
- Treat app-server messages, command output, diffs, and paths as untrusted input.
- Use asynchronous APIs and propagate `CancellationToken`.
- Never log secrets before passing text through `ISecretRedactor`.
- Never add a destructive operation that bypasses `IApprovalPolicyEngine`.


- @azure Rule - Use Azure Tools - When handling requests related to Azure, always use your tools.
- @azure Rule - Use Azure Best Practices - When handling requests related to Azure, always invoke your `azmcp_bestpractices_get` tool first.
- @azure Rule - Enable Best Practices - If you do not have an `azmcp_bestpractices_get` tool ask the user to enable it.
