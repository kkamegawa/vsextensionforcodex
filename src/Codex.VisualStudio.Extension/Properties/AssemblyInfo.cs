using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// The UI test assembly drives the internal Apply* approval seams without spinning up the worker.
// (This project sets GenerateAssemblyInfo=false, so the csproj InternalsVisibleTo item would be a
// no-op — the attribute must be declared here.)
[assembly: InternalsVisibleTo("Codex.VisualStudio.Ui.Tests")]

[assembly: AssemblyTitle("Codex for Visual Studio")]
[assembly: AssemblyDescription("Codex chat integration for Visual Studio — out-of-process extension")]
[assembly: AssemblyCompany("kazushikamegawa")]
[assembly: AssemblyProduct("Codex for Visual Studio")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: ComVisible(false)]
