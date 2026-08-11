using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoCreateProductRequest
{
    [Required]
    public string Name { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Price { get; set; }

    public string? ImageUrl { get; set; }
}

public class TandoProductResponse
{
    public string Id { get; set; }
    public string Name { get; set; }
    public decimal? Price { get; set; }
    public string? ImageUrl { get; set; }
}
