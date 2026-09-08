using Rag.Domain;

namespace Rag.UnitTests;

public sealed class AdminActorTests
{
    [Fact]
    public void Actors_with_the_same_subject_and_app_are_equal()
    {
        var first = new AdminActor("subject-1", "app-1");
        var second = new AdminActor("subject-1", "app-1");

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Actors_with_a_different_subject_or_app_are_not_equal()
    {
        var first = new AdminActor("subject-1", "app-1");

        Assert.NotEqual(first, new AdminActor("subject-2", "app-1"));
        Assert.NotEqual(first, new AdminActor("subject-1", "app-2"));
        Assert.False(first == new AdminActor("subject-2", "app-1"));
    }

    [Fact]
    public void Actor_rejects_a_blank_subject_or_app()
    {
        Assert.Throws<ArgumentException>(() => new AdminActor("", "app-1"));
        Assert.Throws<ArgumentException>(() => new AdminActor("subject-1", "  "));
        Assert.Throws<ArgumentException>(() => new AdminActor("  ", "  "));
    }

    [Fact]
    public void Actor_trims_surrounding_whitespace()
    {
        var actor = new AdminActor("  subject-1  ", "  app-1  ");

        Assert.Equal("subject-1", actor.ActorSubject);
        Assert.Equal("app-1", actor.AppId);
    }
}
