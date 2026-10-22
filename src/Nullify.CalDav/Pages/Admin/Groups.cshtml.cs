using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;
using Group = Nullify.CalDav.Data.Group;

namespace Nullify.CalDav.Pages.Admin;

public class GroupsModel(AppDbContext db, WriteGate gate) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }
    [BindProperty, Required, Display(Name = "Name"), StringLength(100)] public string DisplayName { get; set; } = "";
    [BindProperty] public List<int> Members { get; set; } = [];

    public List<Group> Groups { get; private set; } = [];
    public List<User> Users { get; private set; } = [];
    public Group? Editing { get; private set; }

    private async Task LoadAsync()
    {
        Groups = await db.Groups.Include(g => g.Members).ThenInclude(m => m.User).OrderBy(g => g.DisplayName).ToListAsync();
        Users = await db.Users.OrderBy(u => u.Username).ToListAsync();
        Editing = Id == null ? null : Groups.FirstOrDefault(g => g.Id == Id);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadAsync();
        if (Id != null && Editing == null) return NotFound();
        if (Editing != null)
        {
            DisplayName = Editing.DisplayName;
            Members = Editing.Members.Select(m => m.UserId).ToList();
        }
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        await LoadAsync();
        if (!ModelState.IsValid) return Page();
        using (await gate.EnterAsync())
        {
            var group = Editing;
            if (group == null)
            {
                var baseName = Names.Slugify(DisplayName, "group");
                var name = baseName;
                for (var i = 2; await db.Groups.AnyAsync(g => g.Name == name); i++) name = $"{baseName}-{i}";
                group = new Group { Name = name };
                db.Groups.Add(group);
            }
            group.DisplayName = DisplayName.Trim();
            var wanted = Members.Distinct().Where(id => Users.Any(u => u.Id == id)).ToHashSet();
            group.Members.RemoveAll(m => !wanted.Contains(m.UserId));
            foreach (var id in wanted.Where(id => group.Members.All(m => m.UserId != id)))
                group.Members.Add(new GroupMember { UserId = id });
            await db.SaveChangesAsync();
            StatusMessage = $"Group \"{group.DisplayName}\" saved.";
            return RedirectToPage(new { id = group.Id });
        }
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        using (await gate.EnterAsync())
            await db.Groups.Where(g => g.Id == Id).ExecuteDeleteAsync();
        StatusMessage = "Group deleted; calendars and address books shared with it are no longer shared.";
        return RedirectToPage(new { id = (int?)null });
    }
}
