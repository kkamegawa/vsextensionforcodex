using Codex.VisualStudio.Extension;

namespace Codex.VisualStudio.Ui.Tests;

[TestClass]
public sealed class RemoteProfilePresentationTests
{
    [TestMethod]
    public void Constructor_RestoresSelectedProfileWithoutRewritingSettings()
    {
        ExtensionSettings settings = SavedSettings();
        var store = new RecordingSettingsStore();

        var presentation = new RemoteProfilesPresentationViewModel(settings, store);

        Assert.IsNotNull(presentation.SelectedProfile);
        Assert.AreEqual("Saved profile", presentation.SelectedProfile.Name);
        Assert.IsTrue(presentation.SelectedProfile.IsSelected);
        Assert.IsNotNull(presentation.AddCommand);
        Assert.IsNotNull(presentation.RemoveCommand);
        Assert.IsNotNull(presentation.SaveCommand);
        Assert.IsTrue(presentation.HasSelection);
        Assert.AreEqual(0, store.SaveCount);
    }

    [TestMethod]
    public void ChangingSelection_DoesNotPersistUnvalidatedEdits()
    {
        ExtensionSettings settings = SavedSettings();
        settings.RemoteProfiles.Add(new RemoteConnectionProfile
        {
            Name = "Second",
            Endpoint = "wss://second.example.invalid",
            LocalRoot = "C:\\second",
            ServerRoot = "/second",
        });
        var store = new RecordingSettingsStore();
        var presentation = new RemoteProfilesPresentationViewModel(settings, store);

        presentation.SelectedProfile!.Endpoint = "ws://remote.example.invalid";
        presentation.SelectedProfile.Name = "Second";
        presentation.SelectedProfile = presentation.Profiles[1];

        Assert.AreEqual("wss://example.invalid", store.Saved!.RemoteProfiles[0].Endpoint);
        Assert.AreEqual("Saved profile", store.Saved.RemoteProfiles[0].Name);
        Assert.AreEqual("Second", store.Saved.SelectedRemoteProfileName);

        presentation.SelectedProfile = presentation.Profiles[0];
        presentation.SaveCommand.Execute(null);

        Assert.AreEqual("Use a wss endpoint. Plain ws is allowed only for loopback.", presentation.StatusText);
        Assert.AreEqual("wss://example.invalid", store.Saved.RemoteProfiles[0].Endpoint);
    }

    [TestMethod]
    public void Save_RejectsDuplicateNameAndPersistsValidatedProfile()
    {
        var store = new RecordingSettingsStore();
        var presentation = new RemoteProfilesPresentationViewModel(SavedSettings(), store);

        presentation.AddCommand.Execute(null);
        RemoteProfileViewModel added = presentation.SelectedProfile!;
        Assert.AreEqual(1, store.Saved!.RemoteProfiles.Count);
        Assert.IsNull(store.Saved.SelectedRemoteProfileName);

        added.Name = "saved PROFILE";
        added.Endpoint = "ws://[::1]:4500";
        added.LocalRoot = "C:\\workspace";
        added.ServerRoot = "/workspace";
        presentation.SaveCommand.Execute(null);
        Assert.AreEqual("Profile names must be unique.", presentation.StatusText);
        Assert.AreEqual(1, store.Saved.RemoteProfiles.Count);

        added.Name = "Loopback";
        presentation.SaveCommand.Execute(null);
        Assert.AreEqual(2, store.Saved.RemoteProfiles.Count);
        Assert.AreEqual("Loopback", store.Saved.SelectedRemoteProfileName);
    }

    [TestMethod]
    public void Remove_KeepsOnlySavedProfiles()
    {
        var store = new RecordingSettingsStore();
        var presentation = new RemoteProfilesPresentationViewModel(SavedSettings(), store);
        presentation.AddCommand.Execute(null);

        presentation.SelectedProfile = presentation.Profiles[0];
        presentation.RemoveCommand.Execute(null);

        Assert.AreEqual(1, presentation.Profiles.Count);
        Assert.AreEqual(0, store.Saved!.RemoteProfiles.Count);
        Assert.IsNull(store.Saved.SelectedRemoteProfileName);
    }

    [TestMethod]
    public void RowStatusAndApplicability_TrackUnsavedAndDisabledProfiles()
    {
        var store = new RecordingSettingsStore();
        var presentation = new RemoteProfilesPresentationViewModel(SavedSettings(), store);
        RemoteProfileViewModel saved = presentation.SelectedProfile!;
        Assert.AreEqual(string.Empty, saved.RowStatusText);
        Assert.IsTrue(presentation.TryGetApplicableProfile(out string name, out _));
        Assert.AreEqual("Saved profile", name);
        Assert.AreEqual("Saved profile", presentation.AppliedProfileName);

        saved.ServerRoot = "/moved";
        Assert.AreEqual("Unsaved changes", saved.RowStatusText);
        Assert.IsFalse(presentation.TryGetApplicableProfile(out _, out string unsavedError));
        Assert.AreEqual("Save the profile before connecting with it.", unsavedError);

        saved.ServerRoot = "/workspace";
        saved.IsEnabled = false;
        presentation.SaveCommand.Execute(null);
        Assert.AreEqual("Disabled", saved.RowStatusText);
        Assert.IsFalse(presentation.TryGetApplicableProfile(out _, out string disabledError));
        StringAssert.Contains(disabledError, "Enable the profile");
        Assert.IsNull(presentation.AppliedProfileName);

        presentation.AddCommand.Execute(null);
        Assert.AreEqual("Not saved", presentation.SelectedProfile!.RowStatusText);
        Assert.IsFalse(presentation.HasNoProfiles);

        presentation.ClearSelection();
        Assert.IsFalse(presentation.HasSelection);
        Assert.IsNull(store.Saved!.SelectedRemoteProfileName);
    }

    private static ExtensionSettings SavedSettings() => new()
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

    private sealed class RecordingSettingsStore : IExtensionSettingsStore
    {
        public int SaveCount { get; private set; }

        public ExtensionSettings? Saved { get; private set; }

        public ExtensionSettings Load() => new();

        // Snapshot the profiles so later in-memory edits cannot masquerade as persisted values.
        public void Save(ExtensionSettings settings)
        {
            SaveCount++;
            Saved = new ExtensionSettings
            {
                SelectedRemoteProfileName = settings.SelectedRemoteProfileName,
                RemoteProfiles = settings.RemoteProfiles.Select(static profile => profile.Clone()).ToList(),
            };
        }
    }
}
