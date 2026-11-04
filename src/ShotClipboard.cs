using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenLine
{
    // Builds the payload used by both the single shot card and the basket footer.
    // A single file keeps the picture formats so pasting behaves like a screenshot;
    // several files only expose the file list, which is what WeChat and QQ accept.
    internal static class ShotClipboard
    {
        internal static DataObject Build(IList<string> paths, bool single)
        {
            var payload = new DataObject();
            if (single && paths.Count == 1)
            {
                using (var stream = new FileStream(paths[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                    payload.SetImage(image);
                    payload.SetData("PNG", new MemoryStream(ImageContent.EncodeClipboard(image)));
                }
            }
            var list = new StringCollection();
            foreach (string path in paths) list.Add(path);
            payload.SetFileDropList(list);
            payload.SetData(ClipboardRules.OwnFormat, "1");
            return payload;
        }
    }
}
