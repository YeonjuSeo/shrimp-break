using System;
using System.Drawing;
using System.IO;
using System.Threading;

namespace ShrimpBreak
{
    /// <summary>exe 안에 넣어 둔 이미지 읽기.</summary>
    static class Resources
    {
        public static Image LoadImage(string name)
        {
            var stream = typeof(Resources).Assembly.GetManifestResourceStream(name)
                         ?? throw new FileNotFoundException($"embedded resource {name} is missing");
            // Image 는 원본 스트림을 계속 참조하므로 메모리로 복사해서 독립시킨다.
            using (stream)
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                copy.Position = 0;
                return Image.FromStream(copy);
            }
        }
    }

    /// <summary>
    /// 시스템 이벤트(화면 변경 등)는 다른 스레드에서 올 수 있다 → UI 스레드로 넘겨서 처리한다.
    /// </summary>
    static class Ui
    {
        static SynchronizationContext context;

        public static void Initialize(SynchronizationContext uiContext) => context = uiContext;

        public static void Post(Action action) => context.Post(_ => action(), null);
    }
}
