namespace DemoApi.Models;

/// <summary>A review of a product, as a client sends it.</summary>
public class ProductReview
{
    /// <summary>Stars from 1 to 5.</summary>
    public int Rating { get; set; }

    /// <summary>What the reviewer thinks of the product.</summary>
    public string Comment { get; set; } = "";
}

/// <summary>A review the API took.</summary>
public class StoredReview
{
    /// <summary>The product the review is of.</summary>
    public Guid ProductId { get; set; }

    /// <summary>Stars from 1 to 5.</summary>
    public int Rating { get; set; }

    /// <summary>What the reviewer thinks of the product.</summary>
    public string Comment { get; set; } = "";

    /// <summary>The Idempotency-Key header the review was sent with, if any.</summary>
    public string? IdempotencyKey { get; set; }
}
