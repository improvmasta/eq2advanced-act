using System;
using System.IO;
using EQ2AdvancedUpdater;

var root = Path.Combine(Path.GetTempPath(), "eq2advanced-updatercheck-" + Guid.NewGuid().ToString("N"));
var folder = Path.Combine(root, "installed");
var stage = Path.Combine(root, "stage");
Directory.CreateDirectory(folder);
Directory.CreateDirectory(stage);
try
{
    File.WriteAllText(Path.Combine(folder, "EQ2Advanced.dll"), "old dll");
    File.WriteAllText(Path.Combine(folder, "EQ2AdvancedUpdater.exe"), "old helper");
    File.WriteAllText(Path.Combine(stage, "EQ2Advanced.dll"), "new dll");
    try { FileSwap.Apply(folder, stage); throw new Exception("missing helper was accepted"); }
    catch (FileNotFoundException) { }
    if (File.ReadAllText(Path.Combine(folder, "EQ2Advanced.dll")) != "old dll"
        || File.ReadAllText(Path.Combine(folder, "EQ2AdvancedUpdater.exe")) != "old helper")
        throw new Exception("rollback did not restore both files");
    File.WriteAllText(Path.Combine(stage, "EQ2AdvancedUpdater.exe"), "new helper");
    FileSwap.Apply(folder, stage);
    if (File.ReadAllText(Path.Combine(folder, "EQ2Advanced.dll")) != "new dll"
        || File.ReadAllText(Path.Combine(folder, "EQ2AdvancedUpdater.exe")) != "new helper")
        throw new Exception("update did not install both files");
    Console.WriteLine("Updater swap and rollback passed.");
}
finally { Directory.Delete(root, true); }
