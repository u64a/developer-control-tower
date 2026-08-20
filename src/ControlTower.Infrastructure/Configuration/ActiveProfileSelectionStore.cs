using System;
using System.IO;
using System.Text;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Infrastructure.Configuration
{
    public sealed class ActiveProfileSelectionStore : IActiveProfileSelectionStore
    {
        private readonly string _selectionPath;

        public ActiveProfileSelectionStore(string selectionPath)
        {
            if (string.IsNullOrWhiteSpace(selectionPath))
            {
                throw new ArgumentException("Active profile selection path is required.", nameof(selectionPath));
            }

            _selectionPath = selectionPath;
        }

        public ActiveProfileSelection Load()
        {
            try
            {
                if (!File.Exists(_selectionPath))
                {
                    return new ActiveProfileSelection();
                }

                var raw = File.ReadAllText(_selectionPath).Trim();
                if (Guid.TryParse(raw, out var id) && id != Guid.Empty)
                {
                    return new ActiveProfileSelection { ProfileId = id };
                }

                return InvalidSelection(
                    "The machine-local active profile selection is invalid. Showing all projects.");
            }
            catch (Exception ex)
            {
                return InvalidSelection(
                    "The machine-local active profile selection could not be read. " +
                    "Showing all projects. " + ex.Message);
            }
        }

        public void Save(Guid profileId)
        {
            if (profileId == Guid.Empty)
            {
                throw new ArgumentException("Active profile id must be a non-empty GUID.", nameof(profileId));
            }

            var directory = Path.GetDirectoryName(_selectionPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = _selectionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    tempPath,
                    profileId.ToString("D"),
                    new UTF8Encoding(false));
                File.Move(tempPath, _selectionPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        private static ActiveProfileSelection InvalidSelection(string message)
        {
            return new ActiveProfileSelection
            {
                Issue = new ValidationIssue(
                    IssueSeverity.Warning,
                    "profiles/selection/invalid",
                    message)
            };
        }
    }
}
