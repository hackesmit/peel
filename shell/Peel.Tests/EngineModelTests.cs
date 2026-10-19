// The contract with the engine, pinned against real `peel options` and
// `peel run` output. If a field is renamed in the registry, these fail here
// rather than silently emptying the wheel on George.
using System.Linq;
using System.Text.Json;
using Peel.Engine;
using Peel.Wheel;
using Xunit;

namespace Peel.Tests;

public class EngineModelTests
{
    // Captured from `peel options` on George, 2026-10-19.
    private const string RealOptions = """
    {"ok": true, "files": ["C:\\peel-dev\\main\\install\\README.md"], "wheels": [{"family": "action", "label": "File actions", "default_modifier": "Alt", "groups": [{"group": "Files", "entries": [{"id": "files.copy_path", "family": "action", "label": "Copy Path", "group": "Files", "inputs": ["*"], "output_ext": null, "mode": "combine", "needs_editor": false, "min_files": 1, "max_files": null, "accepts_dirs": true, "handled_by": "engine", "params": [], "backends": [], "missing_backends": [], "available": true}]}]}], "warnings": []}
    """;

    private const string ThreeWheels = """
    {"ok": true, "files": ["C:\\a\\photo.png"], "wheels": [
      {"family": "convert", "label": "Conversions", "default_modifier": "Shift", "groups": [
        {"group": "Images", "entries": [
          {"id": "img.to_webp", "family": "convert", "label": "WEBP", "group": "Images", "inputs": ["png", "jpg"], "output_ext": "webp", "mode": "each", "needs_editor": false, "min_files": 1, "max_files": null, "accepts_dirs": false, "handled_by": "engine", "params": [], "backends": ["vips"], "missing_backends": [], "available": true},
          {"id": "img.to_pdf", "family": "convert", "label": "PDF", "group": "Images", "inputs": ["png"], "output_ext": "pdf", "mode": "each", "needs_editor": false, "min_files": 1, "max_files": null, "accepts_dirs": false, "handled_by": "engine", "params": [], "backends": [], "missing_backends": [], "available": true}]}]},
      {"family": "tool", "label": "File tools", "default_modifier": "Ctrl", "groups": [
        {"group": "Image", "entries": [
          {"id": "img.crop", "family": "tool", "label": "Crop", "group": "Image", "inputs": ["png"], "output_ext": "same", "mode": "each", "needs_editor": true, "min_files": 1, "max_files": 1, "accepts_dirs": false, "handled_by": "engine", "params": [{"name": "box", "type": "json", "label": "Box", "default": null, "required": true, "choices": [], "minimum": null, "maximum": null, "help": ""}], "backends": [], "missing_backends": [], "available": true}]}]},
      {"family": "action", "label": "File actions", "default_modifier": "Alt", "groups": [
        {"group": "Files", "entries": [
          {"id": "files.terminal", "family": "action", "label": "Terminal", "group": "Files", "inputs": ["*"], "output_ext": null, "mode": "combine", "needs_editor": false, "min_files": 1, "max_files": null, "accepts_dirs": true, "handled_by": "shell", "params": [], "backends": [], "missing_backends": [], "available": true}]}]}
    ], "warnings": []}
    """;

    private static OptionsResult Parse(string json) =>
        JsonSerializer.Deserialize<OptionsResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public void RealEngineOutputParses()
    {
        var options = Parse(RealOptions);
        Assert.True(options.Ok);
        var entry = options.Wheels.Single().Groups.Single().Entries.Single();
        Assert.Equal("files.copy_path", entry.Id);
        Assert.True(entry.AcceptsAny);
        Assert.True(entry.AcceptsDirs);
        Assert.Null(entry.OutputExt);
        Assert.Null(entry.MaxFiles);
        Assert.Equal("combine", entry.Mode);
        Assert.False(entry.IsShellAction);
    }

    [Fact]
    public void EachModifierFindsItsWheel()
    {
        var catalog = WheelCatalog.From(Parse(ThreeWheels));
        Assert.Equal("convert", catalog.FamilyForModifier("Shift"));
        Assert.Equal("tool", catalog.FamilyForModifier("Ctrl"));
        Assert.Equal("action", catalog.FamilyForModifier("Alt"));
        Assert.Null(catalog.FamilyForModifier("Win"));
        Assert.Equal("File tools", catalog.LabelFor("tool"));
    }

    [Fact]
    public void AWheelWithNoValidEntriesStillAnswersItsModifier()
    {
        // `peel options <files>` leaves out a wheel with nothing valid in it.
        // The modifier mapping has to survive that, or Ctrl would stop opening
        // the tools wheel for the rest of the drag.
        var forFiles = WheelCatalog.From(Parse(RealOptions));
        Assert.Null(forFiles.FamilyForModifier("Shift"));
        forFiles.WithModifiersFrom(WheelCatalog.From(Parse(ThreeWheels)));
        Assert.Equal("convert", forFiles.FamilyForModifier("Shift"));
        Assert.Empty(WheelPlanner.Flatten(forFiles.Wheel("convert")));
    }

    [Fact]
    public void EntriesArriveInGroupOrder()
    {
        var catalog = WheelCatalog.From(Parse(ThreeWheels));
        var labels = WheelPlanner.Flatten(catalog.Wheel("convert")).Select(entry => entry.Label);
        Assert.Equal(new[] { "WEBP", "PDF" }, labels);
    }

    [Fact]
    public void ShellHandledEntriesAreMarked()
    {
        var catalog = WheelCatalog.From(Parse(ThreeWheels));
        var terminal = WheelPlanner.Flatten(catalog.Wheel("action")).Single();
        Assert.True(terminal.IsShellAction);
    }

    [Fact]
    public void EditorEntriesCarryTheirParameters()
    {
        var catalog = WheelCatalog.From(Parse(ThreeWheels));
        var crop = WheelPlanner.Flatten(catalog.Wheel("tool")).Single();
        Assert.True(crop.NeedsEditor);
        var box = Assert.Single(crop.Params);
        Assert.Equal("box", box.Name);
        Assert.Equal("json", box.Type);
        Assert.True(box.Required);
    }

    [Fact]
    public void AFailedRunKeepsItsCodeAndMessage()
    {
        var result = JsonSerializer.Deserialize<RunResult>(
            """{"ok": false, "outputs": [], "message": "file not found: C:\\a\\gone.png", "code": "missing_file"}""")!;
        Assert.False(result.Ok);
        Assert.Equal("missing_file", result.Code);
        Assert.Empty(result.Outputs);
        Assert.Contains("gone.png", result.Message);
    }

    [Fact]
    public void AGoodRunListsItsOutputs()
    {
        var result = JsonSerializer.Deserialize<RunResult>(
            """{"ok": true, "outputs": ["C:\\a\\photo.webp"], "message": "Wrote photo.webp", "data": {"clipboard_text": "C:\\a\\photo.png"}}""")!;
        Assert.True(result.Ok);
        Assert.Equal(@"C:\a\photo.webp", Assert.Single(result.Outputs));
        Assert.Equal(@"C:\a\photo.png", result.Data.GetProperty("clipboard_text").GetString());
    }

    [Fact]
    public void TheWheelForADraggedFileKeepsOnlyWhatFits()
    {
        var catalog = WheelCatalog.From(Parse(ThreeWheels));
        var files = new[] { new DraggedFile(@"C:\a\photo.jpg", "jpg", false) };
        var entries = WheelPlanner.Filter(WheelPlanner.Flatten(catalog.Wheel("convert")), files);
        Assert.Equal(new[] { "WEBP" }, entries.Select(entry => entry.Label));
    }
}
