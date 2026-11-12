using AsheTrader.Core.Jobs;

namespace AsheTrader.Workflow;

/// <summary>A recipient-first view over the existing per-item saved job format.</summary>
internal static class RecipientAssignments
{
    public static IEnumerable<ulong> Characters(SavedTransferJob job) =>
        job.ItemDefinitions.SelectMany(item => item.Recipients).Select(rule => rule.CharacterId).Distinct();

    public static IEnumerable<(TransferJobDefinition Item, RecipientRule Rule)> ForCharacter(SavedTransferJob job, ulong characterId) =>
        job.ItemDefinitions.SelectMany(item => item.Recipients.Where(rule => rule.CharacterId == characterId)
            .Select(rule => (item, rule)));

    public static TransferJobDefinition Assign(SavedTransferJob job, ulong characterId, uint itemId, bool highQuality,
        IEnumerable<ulong> defaultSources, ulong quantity = 10)
    {
        ArgumentOutOfRangeException.ThrowIfZero(characterId);
        ArgumentOutOfRangeException.ThrowIfZero(itemId);
        ArgumentOutOfRangeException.ThrowIfZero(quantity);
        var matches = job.ItemDefinitions.Where(item => item.ItemId == itemId && item.HighQuality == highQuality).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("This item and quality have duplicate saved definitions. Remove the duplicate item before adding recipients.");
        var definition = matches.SingleOrDefault();
        if (definition == null)
        {
            var providers = defaultSources.ToList();
            if (job.AdditionalItems.Count == 0 && job.Definition.ItemId == 0 && job.Definition.Recipients.Count == 0)
            {
                definition = job.Definition;
                definition.ItemId = itemId;
                definition.HighQuality = highQuality;
                definition.QuantityMode = JobQuantityMode.DeliverAmount;
                definition.Sources = providers;
            }
            else
            {
                definition = new TransferJobDefinition
                {
                    Name = job.Definition.Name, ItemId = itemId, HighQuality = highQuality,
                    QuantityMode = JobQuantityMode.DeliverAmount, Sources = providers
                };
                job.AdditionalItems.Add(definition);
            }
        }
        if (definition.Recipients.Any(rule => rule.CharacterId == characterId)) return definition;
        var recipient = new RecipientRule { CharacterId = characterId, Quantity = quantity };
        var remainder = definition.Recipients.FindIndex(rule => rule.Quantity == null);
        if (remainder < 0) definition.Recipients.Add(recipient);
        else definition.Recipients.Insert(remainder, recipient);
        return definition;
    }

    public static bool Remove(SavedTransferJob job, Guid itemDefinitionId, ulong characterId)
    {
        var definition = job.ItemDefinitions.FirstOrDefault(item => item.Id == itemDefinitionId);
        if (definition == null || definition.Recipients.RemoveAll(rule => rule.CharacterId == characterId) == 0) return false;
        if (definition.Recipients.Count != 0) return true;
        if (!ReferenceEquals(definition, job.Definition)) job.AdditionalItems.Remove(definition);
        else if (job.AdditionalItems.Count != 0)
        {
            // Promote the existing object and identity; journals and input keys must never be relabelled.
            var jobName = definition.Name;
            job.Definition = job.AdditionalItems[0];
            job.AdditionalItems.RemoveAt(0);
            job.Definition.Name = jobName;
        }
        else
        {
            // An empty editor still needs the original job identity and its configured providers.
            definition.ItemId = 0;
            definition.HighQuality = false;
        }
        return true;
    }

    public static bool MovePriority(TransferJobDefinition item, ulong characterId, int direction)
    {
        if (direction is not (-1 or 1)) return false;
        var index = item.Recipients.FindIndex(rule => rule.CharacterId == characterId);
        var other = index + direction;
        if (index < 0 || other < 0 || other >= item.Recipients.Count
            || item.Recipients[index].Quantity == null || item.Recipients[other].Quantity == null) return false;
        (item.Recipients[index], item.Recipients[other]) = (item.Recipients[other], item.Recipients[index]);
        return true;
    }

    public static bool MoveItem(SavedTransferJob job, Guid itemDefinitionId, int direction)
    {
        if (direction is not (-1 or 1)) return false;
        var items = job.ItemDefinitions.ToList();
        var index = items.FindIndex(item => item.Id == itemDefinitionId);
        var other = index + direction;
        if (index < 0 || other < 0 || other >= items.Count) return false;
        var name = job.Definition.Name;
        (items[index], items[other]) = (items[other], items[index]);
        job.Definition = items[0];
        job.Definition.Name = name;
        job.AdditionalItems = items.Skip(1).ToList();
        return true;
    }

    public static bool SetMode(TransferJobDefinition item, JobQuantityMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (item.QuantityMode == mode) return false;
        item.QuantityMode = mode;
        if (mode != JobQuantityMode.MoveAll)
            foreach (var recipient in item.Recipients)
                recipient.Quantity ??= 10;
        return true;
    }
}
