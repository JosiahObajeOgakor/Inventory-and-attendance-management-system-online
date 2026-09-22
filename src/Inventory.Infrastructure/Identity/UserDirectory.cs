using Inventory.Application.Queries;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Identity;

public sealed class UserDirectory(IdentityStore store) : IUserDirectory
{
    public async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0) return [];
        return await store.Users.AsNoTracking().Where(u => distinct.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    public async Task<Dictionary<int, UserBrief>> BriefsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0) return [];
        var rows = await (from u in store.Users.AsNoTracking()
                          where distinct.Contains(u.Id)
                          join ur in store.UserRoles on u.Id equals ur.UserId into urs
                          from ur in urs.DefaultIfEmpty()
                          join r in store.Roles on ur.RoleId equals r.Id into rs
                          from r in rs.DefaultIfEmpty()
                          select new { u.Id, u.FullName, Role = r != null ? r.Name : "" }).ToListAsync(ct);
        return rows.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => new UserBrief(g.First().FullName, g.First().Role ?? ""));
    }
}
