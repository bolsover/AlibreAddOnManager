using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AlibreAddOnManager.Models;

namespace AlibreAddOnManager.Helper
{
    /// <summary>
    /// Applies an InstallPlan. Runs elevated: it is the only code that writes to
    /// %ProgramData%\Alibre AddOns, the state file and HKLM.
    /// </summary>
    internal class Installer
    {
        private readonly PlanResult _result;
        private readonly ManagerState _state;
        private List<TrackedAddOn> Tracked => _state.Tracked ?? (_state.Tracked = new List<TrackedAddOn>());
        private List<DisabledAddOn> Disabled => _state.Disabled ?? (_state.Disabled = new List<DisabledAddOn>());

        public Installer(PlanResult result)
        {
            _result = result;
            _state = AddOnScanner.LoadState();
        }

        public void Apply(InstallPlan plan)
        {
            var operations = plan.Operations ?? throw new InvalidDataException("Install plan has no operations.");
            foreach (var op in operations)
            {
                try
                {
                    switch (op.Kind)
                    {
                        case PlanOperationKind.Install: Install(op); break;
                        case PlanOperationKind.Uninstall: Uninstall(op); break;
                        case PlanOperationKind.Disable: Disable(op); break;
                        case PlanOperationKind.Enable: Enable(op); break;
                        case PlanOperationKind.RemoveRegistration: RemoveRegistration(op); break;
                        default: throw new InvalidOperationException("Unknown operation '" + op.Kind + "'.");
                    }

                    _result.Lines.Add("OK      " + op.Describe());
                }
                catch (Exception e)
                {
                    _result.Failures++;
                    _result.Lines.Add("FAILED  " + op.Describe() + ": " + e.Message);
                }

                // Save after every operation so the state always matches what was done.
                Json.WriteFile(ManagerPaths.StateFile, _state);
            }
        }

        private void Install(PlanOperation op)
        {
            var packagePath = Required(op.PackagePath, "packagePath");
            var operationId = Required(op.Id, "id");
            if (!File.Exists(packagePath))
                throw new FileNotFoundException("Package not found: " + packagePath);

            // The plan and package sit in the user's profile; check nothing changed
            // since the unelevated add-on validated them.
            var hash = Packages.Sha256File(packagePath);
            if (!string.Equals(hash, op.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package hash changed since it was validated.");

            var validation = Packages.Validate(packagePath, operationId);
            if (!validation.IsValid)
                throw new InvalidDataException(string.Join(" ", validation.Errors));

            var manifest = validation.Manifest ?? throw new InvalidDataException("Validated package has no manifest.");
            var manifestId = Required(manifest.Id, "manifest id");
            var folderName = Required(manifest.FolderName, "manifest folderName");
            var normalizedId = ManagerPaths.NormalizeId(manifestId) ?? manifestId;
            var root = ManagerPaths.AddOnsRoot;
            var target = Path.Combine(root, folderName);
            if (!ManagerPaths.IsSameOrUnder(target, root) ||
                string.Equals(Path.GetFullPath(target).TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Target folder escapes " + root);

            // Refuse to overwrite a folder another add-on is registered to.
            var registered = AddOnRegistry.ReadAll();
            foreach (var pair in registered)
            {
                if (ManagerPaths.SameId(pair.Key, manifestId) || string.IsNullOrWhiteSpace(pair.Value)) continue;
                if (string.Equals(Path.GetFullPath(pair.Value).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Folder {target} is registered to a different add-on ({pair.Key}).");
            }

            // An existing folder is only replaced if we own it (tracked, or this manager).
            var existingTracked = Tracked.FirstOrDefault(t => ManagerPaths.SameId(t.Id, manifestId));
            if (Directory.Exists(target) && existingTracked == null && !ManagerPaths.SameId(manifestId, ManagerPaths.SelfId))
                throw new InvalidOperationException($"Folder {target} already exists and was not installed by the add-on manager.");

            Directory.CreateDirectory(root);
            var staging = target + ".staging";
            var backup = target + ".bak";
            DeleteDirectory(staging);
            Packages.ExtractSafely(packagePath, staging);

            if (ManagerPaths.SameId(manifestId, ManagerPaths.SelfId))
                CarryOverInnoUninstaller(target, staging);

            var movedOld = false;
            try
            {
                if (Directory.Exists(target))
                {
                    DeleteDirectory(backup);
                    Directory.Move(target, backup); // fails if a DLL in it is locked
                    movedOld = true;
                }

                Directory.Move(staging, target);
            }
            catch
            {
                if (movedOld && !Directory.Exists(target))
                    Directory.Move(backup, target);
                DeleteDirectory(staging);
                throw;
            }

            AddOnRegistry.SetValue(normalizedId, target);

            Tracked.RemoveAll(t => ManagerPaths.SameId(t.Id, manifestId));
            Disabled.RemoveAll(d => ManagerPaths.SameId(d.Id, manifestId));
            Tracked.Add(new TrackedAddOn
            {
                Id = normalizedId,
                Name = manifest.Name,
                Version = manifest.Version,
                Folder = target,
                Source = op.Source,
                Sha256 = hash,
                InstalledUtc = DateTime.UtcNow.ToString("o")
            });

            TryDeleteDirectory(backup);
        }

        private void Uninstall(PlanOperation op)
        {
            var operationId = Required(op.Id, "id");
            var tracked = Tracked.FirstOrDefault(t => ManagerPaths.SameId(t.Id, operationId));
            if (tracked == null)
                throw new InvalidOperationException("Not installed by the add-on manager; only Disable is allowed.");

            if (ManagerPaths.SameId(operationId, ManagerPaths.SelfId))
                throw new InvalidOperationException("The add-on manager cannot uninstall itself; use its Windows uninstaller.");

            // Delete only a folder we created, and only inside our own root.
            string? removing = null;
            var trackedFolder = Required(tracked.Folder, "tracked folder");
            if (Directory.Exists(trackedFolder))
            {
                if (!ManagerPaths.IsSameOrUnder(trackedFolder, ManagerPaths.AddOnsRoot))
                    throw new InvalidOperationException("Tracked folder is outside " + ManagerPaths.AddOnsRoot + "; files left in place.");

                // Rename first: it fails as a whole if a DLL is locked, rather than
                // leaving a half-deleted folder that Alibre is still registered to.
                removing = trackedFolder.TrimEnd('\\') + ".removing";
                DeleteDirectory(removing);
                Directory.Move(trackedFolder, removing);
            }

            AddOnRegistry.DeleteValue(operationId);
            Tracked.Remove(tracked);
            Disabled.RemoveAll(d => ManagerPaths.SameId(d.Id, operationId));

            if (removing != null)
                TryDeleteDirectory(removing); // unregistered by now; a leftover is harmless
        }

        /// <summary>
        /// A self-update replaces the whole folder, which would delete the Inno Setup
        /// uninstaller the manager was installed with. Copy it into the new folder.
        /// </summary>
        private static void CarryOverInnoUninstaller(string target, string staging)
        {
            if (!Directory.Exists(target)) return;
            foreach (var file in Directory.GetFiles(target, ManagerPaths.InnoUninstallerPattern))
            {
                var destination = Path.Combine(staging, Path.GetFileName(file));
                if (!File.Exists(destination))
                    File.Copy(file, destination);
            }
        }

        private void Disable(PlanOperation op)
        {
            var operationId = Required(op.Id, "id");
            var registered = AddOnRegistry.ReadAll();
            var name = AddOnRegistry.FindValueName(registered, operationId);
            if (name == null)
                throw new InvalidOperationException("Not currently registered.");

            _state.Disabled = Disabled;
            Disabled.RemoveAll(d => ManagerPaths.SameId(d.Id, operationId));
            Disabled.Add(new DisabledAddOn
            {
                Id = name, // keep the exact value name so Enable restores it verbatim
                Name = op.Name,
                Folder = registered[name],
                DisabledUtc = DateTime.UtcNow.ToString("o")
            });
            Json.WriteFile(ManagerPaths.StateFile, _state); // record before removing
            AddOnRegistry.DeleteValue(name);
        }

        private void Enable(PlanOperation op)
        {
            var operationId = Required(op.Id, "id");
            var disabled = Disabled.FirstOrDefault(d => ManagerPaths.SameId(d.Id, operationId));
            if (disabled == null)
                throw new InvalidOperationException("No record of this add-on being disabled.");

            AddOnRegistry.SetValue(Required(disabled.Id, "disabled id"), Required(disabled.Folder, "disabled folder"));
            Disabled.Remove(disabled);
        }

        private void RemoveRegistration(PlanOperation op)
        {
            if (!AddOnRegistry.DeleteValue(Required(op.Id, "id")))
                throw new InvalidOperationException("Not currently registered.");
        }

        private static string Required(string? value, string name)
        {
            if (value == null || string.IsNullOrWhiteSpace(value))
                throw new InvalidDataException("Install plan is missing '" + name + "'.");
            return value;
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }

        private static void TryDeleteDirectory(string path)
        {
            try { DeleteDirectory(path); }
            catch (Exception) { /* a leftover .bak / .removing is harmless */ }
        }
    }
}
