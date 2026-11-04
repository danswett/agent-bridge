using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class AtomicPreferencesTests
{
    [Fact]
    public void Interrupted_save_preserves_the_previous_preferences_and_removes_partial_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var expected = new AppPreferences();
            expected.Save(path);
            var original = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => AtomicPreferences.Write(path, stream =>
            {
                stream.Write("partial"u8);
                throw new IOException("disk full");
            }));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Single(dir.GetFiles());
        }
        finally { dir.Delete(true); }
    }
}
