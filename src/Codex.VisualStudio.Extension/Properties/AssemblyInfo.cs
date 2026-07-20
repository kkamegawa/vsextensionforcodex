using System.Runtime.CompilerServices;

// The UI test assembly drives the internal Apply* approval seams without spinning up the worker.
// Title, product, company, version and ComVisible are generated from the project file so the
// release workflow can set the VSIX version from the git tag.
[assembly: InternalsVisibleTo("Codex.VisualStudio.Ui.Tests")]
