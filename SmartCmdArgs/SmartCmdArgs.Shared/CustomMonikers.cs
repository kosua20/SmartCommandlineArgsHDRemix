using Microsoft.VisualStudio.Imaging.Interop;
using System;

namespace SmartCmdArgs
{
    internal class CustomMonikers
    {
        private static readonly Guid ManifestGuid = new Guid( "25e29480-bf22-41dc-8276-bd488424d8fa" );

        public static ImageMoniker FoProjectNode => new ImageMoniker { Guid = ManifestGuid, Id = 0 };
        public static ImageMoniker CopyCmdLine => new ImageMoniker { Guid = ManifestGuid, Id = 1 };
    }
}
