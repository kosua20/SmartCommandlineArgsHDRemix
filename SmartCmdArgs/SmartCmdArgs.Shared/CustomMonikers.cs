using Microsoft.VisualStudio.Imaging.Interop;
using System;

namespace SmartCmdArgs
{
    internal class CustomMonikers
    {
        private static readonly Guid ManifestGuid = new Guid( "456afed8-eeb0-4e2d-bb64-c74a4603a623" );

        public static ImageMoniker FoProjectNode => new ImageMoniker { Guid = ManifestGuid, Id = 0 };
        public static ImageMoniker CopyCmdLine => new ImageMoniker { Guid = ManifestGuid, Id = 1 };
    }
}
