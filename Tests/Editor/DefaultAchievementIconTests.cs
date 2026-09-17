using DryreLHub.SupabaseGameAchievements.Unity;
using NUnit.Framework;

namespace DryreLHub.SupabaseGameAchievements.Tests
{
    public class DefaultAchievementIconTests
    {
        [Test]
        public void Returns_a_usable_sprite()
        {
            var sprite = DefaultAchievementIcon.GetOrCreate();
            Assert.IsNotNull(sprite);
            Assert.IsNotNull(sprite.texture);
            Assert.Greater(sprite.texture.width, 0);
            Assert.Greater(sprite.texture.height, 0);
        }

        [Test]
        public void Is_built_once_and_reused()
        {
            Assert.AreSame(DefaultAchievementIcon.GetOrCreate(), DefaultAchievementIcon.GetOrCreate());
        }
    }
}
