using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class RemoteProfilePresentationTests
{
    [TestMethod]
    public void Constructor_RestoresSelectedProfileAfterCommandsAreInitialized()
    {
        var settings = new ExtensionSettings
        {
            SelectedRemoteProfileName = "Saved profile",
            RemoteProfiles =
            [
                new RemoteConnectionProfile
                {
                    Name = "Saved profile",
                    Endpoint = "wss://example.invalid",
                    LocalRoot = "C:\\workspace",
                    ServerRoot = "/workspace",
                    Enabled = true,
                    TokenFilePath = "C:\\tokens\\codex.token",
                },
            ],
        };
        var store = new RecordingSettingsStore();

        var presentation = new RemoteProfilesPresentationViewModel(settings, store);

        Assert.IsNotNull(presentation.SelectedProfile);
        Assert.AreEqual("Saved profile", presentation.SelectedProfile.Name);
        Assert.IsTrue(presentation.SelectedProfile.IsSelected);
        Assert.IsNotNull(presentation.AddCommand);
        Assert.IsNotNull(presentation.RemoveCommand);
        Assert.IsNotNull(presentation.SaveCommand);
        Assert.IsTrue(presentation.HasSelection);
        Assert.IsTrue(store.SaveCount > 0);
    }

    private sealed class RecordingSettingsStore : IExtensionSettingsStore
    {
        public int SaveCount { get; private set; }

        public ExtensionSettings Load() => new();

        public void Save(ExtensionSettings settings) => SaveCount++;
    }
}
