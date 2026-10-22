// Copyright 2026 Simon Soanes
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nullify.CalDav.Dav;
using Nullify.CalDav.Data;
using Nullify.CalDav.Services;

namespace Nullify.CalDav.Pages.AddressBooks;

public class EditModel(AppDbContext db, AddressBookStore store, WriteGate gate) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public int? Id { get; set; }

    [BindProperty, Required, Display(Name = "Name"), StringLength(200)] public string DisplayName { get; set; } = "";
    [BindProperty, StringLength(2000)] public string? Description { get; set; }
    [BindProperty, Display(Name = "URL name")] public string? Uri { get; set; }

    [BindProperty, Display(Name = "User")] public string? ShareWith { get; set; }
    [BindProperty] public ShareAccess ShareAccess { get; set; } = ShareAccess.Read;

    public AddressBook? Book { get; private set; }
    public int CardCount { get; private set; }
    public List<AddressBookShare> Shares { get; private set; } = [];
    public List<User> OtherUsers { get; private set; } = [];
    public List<Group> Groups { get; private set; } = [];
    public bool IsDirectory => Book?.Kind == AddressBookKind.Directory;
    public string? CollectionUrl { get; private set; }

    private async Task<AddressBook?> LoadAsync()
    {
        if (Id == null) return null;
        Book = await db.AddressBooks.FirstOrDefaultAsync(b => b.Id == Id && b.OwnerId == CurrentUserId);
        if (Book == null) return null;
        CardCount = await db.Cards.CountAsync(c => c.AddressBookId == Book.Id);
        Shares = await db.AddressBookShares.Include(s => s.Sharee).Include(s => s.Group).Where(s => s.AddressBookId == Book.Id)
            .OrderBy(s => s.GroupId == null).ThenBy(s => s.Id).ToListAsync();
        var shared = Shares.Where(s => s.ShareeId != null).Select(s => s.ShareeId!.Value).ToHashSet();
        var sharedGroups = Shares.Where(s => s.GroupId != null).Select(s => s.GroupId!.Value).ToHashSet();
        Groups = (await db.Groups.OrderBy(g => g.DisplayName).ToListAsync()).Where(g => !sharedGroups.Contains(g.Id)).ToList();
        OtherUsers = (await db.Users.Where(u => u.Id != CurrentUserId && u.IsEnabled).OrderBy(u => u.Username).ToListAsync())
            .Where(u => !shared.Contains(u.Id)).ToList();
        var me = await db.Users.Where(u => u.Id == CurrentUserId).Select(u => u.Username).FirstAsync();
        CollectionUrl = new DavPaths(ServerUrl + "/dav").AddressBook(me, Book.Uri);
        return Book;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (Id == null) return Page();
        var book = await LoadAsync();
        if (book == null) return NotFound();
        DisplayName = book.DisplayName;
        Description = book.Description;
        Uri = book.Uri;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var book = await LoadAsync();
        if (Id != null && book == null) return NotFound();
        if (!ModelState.IsValid) return Page();

        using (await gate.EnterAsync())
        {
            if (book == null)
            {
                var requested = string.IsNullOrWhiteSpace(Uri) ? Names.Slugify(DisplayName, "contacts") : Uri.Trim();
                if (!Names.IsValidSegment(requested))
                {
                    ModelState.AddModelError(nameof(Uri), "Invalid URL name.");
                    return Page();
                }
                if (!string.IsNullOrWhiteSpace(Uri) && await store.HomeUriTakenAsync(CurrentUserId, requested))
                {
                    ModelState.AddModelError(nameof(Uri), "That URL name is already in use.");
                    return Page();
                }
                var uri = await store.UniqueHomeUriAsync(CurrentUserId, requested);
                book = await store.CreateAsync(CurrentUserId, uri, DisplayName.Trim(), Description);
                StatusMessage = "Address book created.";
            }
            else
            {
                book.DisplayName = DisplayName.Trim();
                book.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
                book.ModifiedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync();
                StatusMessage = "Address book saved.";
            }
        }
        return RedirectToPage(new { id = book.Id });
    }

    public async Task<IActionResult> OnPostShareAsync()
    {
        var book = await LoadAsync();
        if (book == null) return NotFound();
        var target = (ShareWith ?? "").Trim();
        if (target.StartsWith("group:", StringComparison.Ordinal))
        {
            var groupName = target[6..];
                var group = await db.Groups.FirstOrDefaultAsync(g => g.Name == groupName);
            if (group == null) { ErrorMessage = "Select a valid group."; return RedirectToPage(new { id = Id }); }
            using (await gate.EnterAsync()) await store.ShareWithGroupAsync(book, group, ShareAccess);
            StatusMessage = $"Shared with group {group.DisplayName}.";
            return RedirectToPage(new { id = Id });
        }
        var sharee = await db.Users.FirstOrDefaultAsync(u => u.Username == target.Replace("user:", "") && u.IsEnabled);
        if (sharee == null || sharee.Id == CurrentUserId)
        {
            ErrorMessage = "Select a valid user to share with.";
            return RedirectToPage(new { id = Id });
        }
        using (await gate.EnterAsync()) await store.ShareAsync(book, sharee, ShareAccess);
        StatusMessage = $"Shared with {sharee.DisplayName}.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostAccessAsync(int shareId, ShareAccess access)
    {
        var book = await LoadAsync();
        if (book == null) return NotFound();
        var share = Shares.FirstOrDefault(s => s.Id == shareId);
        if (share != null)
        {
            share.Access = access;
            using (await gate.EnterAsync()) await db.SaveChangesAsync();
            StatusMessage = "Access updated.";
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostUnshareAsync(int shareId)
    {
        var book = await LoadAsync();
        if (book == null) return NotFound();
        using (await gate.EnterAsync())
            await db.AddressBookShares.Where(s => s.Id == shareId && s.AddressBookId == book.Id).ExecuteDeleteAsync();
        StatusMessage = "Sharing removed.";
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file)
    {
        var book = await LoadAsync();
        if (book == null || IsDirectory) return NotFound();
        if (file == null || file.Length == 0)
        {
            ErrorMessage = "Choose a .vcf file to import.";
            return RedirectToPage(new { id = Id });
        }
        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, true)) text = await reader.ReadToEndAsync();
        CalendarStore.ImportResult result;
        using (await gate.EnterAsync()) result = await store.ImportAsync(book, text);
        StatusMessage = $"Import finished: {result.Imported} added, {result.Updated} updated, {result.Skipped} skipped.";
        if (result.Errors.Count > 0) ErrorMessage = string.Join(" ", result.Errors.Take(5));
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        var book = await LoadAsync();
        if (book == null) return NotFound();
        var vcf = await store.ExportAsync(book);
        return File(Encoding.UTF8.GetBytes(vcf), "text/vcard", Names.Slugify(book.DisplayName, "contacts") + ".vcf");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var book = await LoadAsync();
        if (book == null || IsDirectory) return NotFound();
        using (await gate.EnterAsync()) await store.DeleteAsync(book);
        StatusMessage = $"Address book \"{book.DisplayName}\" moved to the trash.";
        return RedirectToPage("Index");
    }
}
