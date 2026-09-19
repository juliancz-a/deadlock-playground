using System;
using System.IO;
using ValveResourceFormat;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;

namespace DeadlockPlayground.Tools
{
    /// <summary>
    /// Composite IFileLoader implementing priority fallback between an active Addon VPK (Priority 1)
    /// and the Base Game VPK (Priority 2), allowing VRF exporters and anim loaders to resolve
    /// overridden assets transparently.
    /// </summary>
    public class DualLayerGameFileLoader : IFileLoader
    {
        private readonly IFileLoader _primary;
        private readonly IFileLoader _secondary;

        public DualLayerGameFileLoader(IFileLoader primary, IFileLoader secondary)
        {
            _primary = primary;
            _secondary = secondary;
        }

        public Resource LoadFile(string file)
        {
            if (_primary != null)
            {
                try
                {
                    var res = _primary.LoadFile(file);
                    if (res != null) return res;
                }
                catch
                {
                    // Fall back to secondary
                }
            }

            return _secondary?.LoadFile(file);
        }

        public Resource LoadFileCompiled(string file)
        {
            if (_primary != null)
            {
                try
                {
                    var res = _primary.LoadFileCompiled(file);
                    if (res != null) return res;
                }
                catch
                {
                    // Fall back to secondary
                }
            }

            return _secondary?.LoadFileCompiled(file);
        }

        public ShaderCollection LoadShader(string shaderName)
        {
            if (_primary != null)
            {
                try
                {
                    var shader = _primary.LoadShader(shaderName);
                    if (shader != null) return shader;
                }
                catch
                {
                    // Fall back to secondary
                }
            }

            return _secondary?.LoadShader(shaderName);
        }

        public Stream GetFileStream(string file)
        {
            if (_primary != null)
            {
                try
                {
                    var stream = _primary.GetFileStream(file);
                    if (stream != null) return stream;
                }
                catch
                {
                    // Fall back to secondary
                }
            }

            return _secondary?.GetFileStream(file);
        }
    }
}
