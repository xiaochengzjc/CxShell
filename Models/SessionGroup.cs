using System;

namespace CxShell.Models;

public class SessionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid? ParentId { get; set; }
    public int SortOrder { get; set; }
}
