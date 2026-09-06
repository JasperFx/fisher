using Fisher.Linq;
using Fisher.Linq.Includes;

namespace Fisher.Tests.Documentation;

/*
 * The compiled source behind docs/documents/querying/linq/includes.md.
 *
 * See "Documentation samples come from compiled code" in CLAUDE.md. Every block a reader would
 * copy whole lives here; the fragment lists on that page stay inline fences, since the surrounding
 * method would be the larger half of what they show.
 */

public class Boat
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Berth { get; set; } = "";
}

public class Crew
{
    public Guid Id { get; set; }
    public Guid BoatId { get; set; }
    public string Name { get; set; } = "";
}

public class IncludedAngler
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Region { get; set; } = "";
    public Guid BoatId { get; set; }
    public List<Guid> FavouriteBoatIds { get; set; } = [];
}

public class AnglersCatch
{
    public Guid Id { get; set; }
    public Guid AnglerId { get; set; }
    public string Species { get; set; } = "";
}

public static class include_samples
{
    public static async Task include_a_related_document(IQuerySession session)
    {
        #region sample_include_a_related_document
        var boats = new List<Boat>();

        var anglers = await session.Query<IncludedAngler>()
            .Where(x => x.Region == "Shire")
            .Include(x => x.BoatId, boats)
            .ToListAsync();
        #endregion

        _ = anglers;
    }

    public static async Task include_grouped_by_the_mapping_member(IQuerySession session)
    {
        #region sample_include_grouped_by_the_mapping_member
        var byAngler = new Dictionary<Guid, List<AnglersCatch>>();

        var anglers = await session.Query<IncludedAngler>()
            .Include(x => x.Id, (AnglersCatch c) => c.AnglerId, byAngler)
            .ToListAsync();
        #endregion

        _ = anglers;
    }

    public static async Task several_includes(IQuerySession session)
    {
        var boats = new List<Boat>();
        var catches = new List<AnglersCatch>();

        #region sample_include_several_at_once
        await session.Query<IncludedAngler>()
            .Include(x => x.BoatId, boats)
            .Include(x => x.Id, (AnglersCatch c) => c.AnglerId, catches)
            .ToListAsync();
        #endregion
    }

    public static async Task include_with_a_filter(IQuerySession session)
    {
        var boats = new List<Boat>();

        #region sample_include_with_a_filter
        await session.Query<IncludedAngler>()
            .Include(x => x.BoatId, boats, b => b.Berth == "Hobbiton")
            .ToListAsync();
        #endregion
    }

    public static async Task include_the_documents_pointing_back(IQuerySession session)
    {
        #region sample_include_the_documents_pointing_back
        var crew = new List<Crew>();

        await session.Query<Boat>()
            .Where(x => x.Name == "Brandywine Belle")
            .Include(x => x.Id, (Crew c) => c.BoatId, crew)
            .ToListAsync();
        #endregion
    }
}
