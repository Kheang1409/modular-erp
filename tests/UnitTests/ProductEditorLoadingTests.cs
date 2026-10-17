using Erp.Modules.Products;
using Erp.Modules.Products.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.UnitTests;

public sealed class ProductEditorLoadingTests
{
    private static readonly Guid ProductId = Guid.Parse("fe99a223-99cc-4616-895b-a4f07ceac9a8");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditPageWaitsForProductReadBeforeLoadingCompanyOptions(bool exists)
    {
        var products = new ControlledProducts();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddSingleton<IProductService>(products);
        registrations.AddSingleton<NavigationManager, TestNavigation>();
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = ProductId });
        var rendering = renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<EditProductPage>(parameters));
        var product = exists ? new ProductDto(ProductId, ProductType.Product, "EDITOR-TEST", "Editor test product",
            null, null, 10m, 5m, "USD", "EA", "EA", true, false,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, "product-version") : null;
        try
        {
            await products.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // A dispatcher turn ensures the initial render has finished its synchronous work.
            await renderer.Dispatcher.InvokeAsync(() => Assert.Equal(["product"], products.Calls));
            products.Completion.TrySetResult(product);
            var component = await rendering.WaitAsync(TimeSpan.FromSeconds(10));
            var html = await renderer.Dispatcher.InvokeAsync(component.ToHtmlString);

            Assert.Equal(["product", "options"], products.Calls);
            Assert.DoesNotContain("Loading product", html);
            if (exists)
            {
                Assert.Contains("Editor test product", html);
                Assert.Contains("<form", html);
                Assert.DoesNotContain("Product not found", html);
            }
            else
            {
                Assert.Contains("Product not found", html);
                Assert.DoesNotContain("<form", html);
            }
        }
        finally
        {
            products.Completion.TrySetResult(product);
        }
    }

    private sealed class ControlledProducts : IProductService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProductDto?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Calls { get; } = [];

        public Task<ProductDto?> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            Assert.Equal(ProductId, id);
            Calls.Add("product");
            Started.TrySetResult();
            return Completion.Task;
        }

        public Task<ProductOptions> GetOptionsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("options");
            return Task.FromResult(new ProductOptions("USD", [new("USD", "US Dollar", "$")], [new("EA", "Each", "Count", 0)]));
        }

        public Task<ProductPage> SearchAsync(ProductQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProductDto> CreateAsync(ProductRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProductDto> UpdateAsync(Guid id, ProductRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProductDto> SetArchivedAsync(Guid id, string concurrencyStamp, bool archived, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProductCsvImportResult> ImportCsvAsync(Stream csv, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ExportCsvAsync(ProductQuery query, Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://erp.example.test/", $"https://erp.example.test/products/{ProductId}");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Loading the editor must not navigate.");
    }
}
