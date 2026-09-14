using Godot;
using System;
using System.IO;

namespace DeadlockPlayground.Painter
{
    public class SkinExportResult
    {
        public bool Success { get; set; }
        public string PngPath { get; set; }
        public string VmatPath { get; set; }
        public string ModDirectory { get; set; }
        public string ErrorMessage { get; set; }
    }

    public partial class SkinExporter : RefCounted
    {
        private readonly SkinLayerManager _layerManager;

        public SkinExporter(SkinLayerManager layerManager)
        {
            _layerManager = layerManager;
        }

        public SkinExportResult ExportFlattenedPng(string filePath)
        {
            var result = new SkinExportResult();

            if (_layerManager == null)
            {
                result.Success = false;
                result.ErrorMessage = "SkinLayerManager reference is null.";
                return result;
            }

            var image = _layerManager.BakeCompositeImage();
            if (image == null)
            {
                result.Success = false;
                result.ErrorMessage = "Failed to capture composite image from viewport.";
                return result;
            }

            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                Error err = image.SavePng(filePath);
                if (err != Error.Ok)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Godot SavePng failed with error code: {err}";
                    return result;
                }

                result.Success = true;
                result.PngPath = filePath;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        public SkinExportResult ExportVmat(string vmatPath, string relativeTexturePath)
        {
            var result = new SkinExportResult();

            try
            {
                string dir = Path.GetDirectoryName(vmatPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string vmatContent = GenerateVmatKv3Content(relativeTexturePath);
                File.WriteAllText(vmatPath, vmatContent);

                result.Success = true;
                result.VmatPath = vmatPath;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        public SkinExportResult InstallModToCitadel(string deadlockGamePath, string heroCodename, string submeshName)
        {
            var result = new SkinExportResult();

            if (string.IsNullOrEmpty(deadlockGamePath))
            {
                result.Success = false;
                result.ErrorMessage = "Citadel game path is not configured.";
                return result;
            }

            string cleanHero = string.IsNullOrEmpty(heroCodename) ? "custom_hero" : heroCodename.ToLowerInvariant();
            string cleanMesh = string.IsNullOrEmpty(submeshName) ? "body" : submeshName.ToLowerInvariant().Replace(" ", "_");

            // Citadel Addons layout: <Deadlock>/game/citadel/addons/deadlock_skins/
            string addonsRoot = Path.Combine(deadlockGamePath, "game", "citadel", "addons", "deadlock_skins");
            string targetDir = Path.Combine(addonsRoot, "materials", "heroes", cleanHero);

            try
            {
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                string pngFileName = $"{cleanHero}_{cleanMesh}_color.png";
                string fullPngPath = Path.Combine(targetDir, pngFileName);

                var pngRes = ExportFlattenedPng(fullPngPath);
                if (!pngRes.Success)
                {
                    return pngRes;
                }

                string vmatFileName = $"{cleanHero}_{cleanMesh}.vmat";
                string fullVmatPath = Path.Combine(targetDir, vmatFileName);
                string relativeTexPath = $"materials/heroes/{cleanHero}/{pngFileName}";

                var vmatRes = ExportVmat(fullVmatPath, relativeTexPath);
                if (!vmatRes.Success)
                {
                    return vmatRes;
                }

                result.Success = true;
                result.PngPath = fullPngPath;
                result.VmatPath = fullVmatPath;
                result.ModDirectory = addonsRoot;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        public static string GenerateVmatKv3Content(string textureResourcePath)
        {
            return 
@"<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
{
	shader = ""citadel_hero.vfx""

	//---- Color ----
	g_vColorTint = [1.000000, 1.000000, 1.000000, 1.000000]
	TextureColor = resource:""" + textureResourcePath.Replace('\\', '/') + @"""

	//---- PBR Defaults ----
	g_flRoughnessScale = 1.000000
	g_flMetalnessScale = 0.000000

	//---- Deadlock Playground Skin Authoring Studio ----
}
";
        }
    }
}
