namespace Accessibility.Domain.Common;

public abstract class Entity
{
    protected Entity()
    {
        Id = Guid.CreateVersion7();
    }

    public Guid Id { get; private set; }
}

public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedAt { get; internal set; }

    public DateTimeOffset UpdatedAt { get; internal set; }

    public long Version { get; internal set; } = 1;
}
