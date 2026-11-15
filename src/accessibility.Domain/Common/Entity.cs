namespace Accessibility.Domain.Common;

public abstract class Entity
{
    protected Entity()
    {
        Id = Guid.CreateVersion7();
    }

    protected Entity(Guid id)
    {
        Id = id != Guid.Empty ? id : throw new ArgumentException("Entity ID is required.", nameof(id));
    }

    public Guid Id { get; private set; }
}

public abstract class AuditableEntity : Entity
{
    protected AuditableEntity()
    {
    }

    protected AuditableEntity(Guid id) : base(id)
    {
    }

    public DateTimeOffset CreatedAt { get; internal set; }

    public DateTimeOffset UpdatedAt { get; internal set; }

    public long Version { get; internal set; } = 1;
}
