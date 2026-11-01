using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var services = builder.Services;
// Свой провайдер добавляется в конфигурацию. Он идёт после файлов и переменных окружения,
// поэтому его значения перекрывают остальные (последний источник побеждает).
var manualConfig = new ManualConfigurationSource();
((IConfigurationBuilder)cfg).Add(manualConfig);

// Источник токена независимо от конфигурации
var manualToken = new ManualChangeTokenSource<ApiOptions>();
services.AddSingleton<IOptionsChangeTokenSource<ApiOptions>>(manualToken);

services.AddOptions();

// ---------- 1. Участники конвейера ----------
// Порядок регистрации = порядок вызова внутри фазы.

// #1: для ЛЮБОГО имени
services.ConfigureAll<ApiOptions>(o =>
{
    DemoLog.Write("  [Configure #1] ConfigureAll lambda: baseline для любого имени");
    o.Limit = 1;
    o.Title = "baseline";
});

// #2: обычный Configure. Применяется только к имени по умолчанию ("")
services.Configure<ApiOptions>(o =>
{
    DemoLog.Write("  [Configure #2] plain lambda: только для имени по умолчанию");
    o.Limit = 10;
    o.Title = "from-code";
});

// #3: привязка секции "Api" к имени по умолчанию (Options.ConfigurationExtensions).
// Заодно регистрирует IOptionsChangeTokenSource, поэтому монитор узнает о перезагрузке файла.
services.Configure<ApiOptions>(cfg.GetSection("Api"));

// #4: привязка секции "Billing" к имени "billing"
services.Configure<ApiOptions>("billing", cfg.GetSection("Billing"));

// #5: имя "broken" с заведомо неверным значением
services.Configure<ApiOptions>("broken", o =>
{
    DemoLog.Write("  [Configure #5] lambda для 'broken': Limit = -1");
    o.Limit = -1;
});

// #6: свой класс, показывает состояние после всех предыдущих
services.AddSingleton<IConfigureOptions<ApiOptions>, TraceNamedConfigure>();

services.AddSingleton<IPostConfigureOptions<ApiOptions>, TracePostConfigure>();
services.AddSingleton<IValidateOptions<ApiOptions>, LimitValidator>();

// ---------- 2. Подмена фабрики ----------
services.AddTransient(typeof(OptionsFactory<>));
services.Replace(ServiceDescriptor.Transient(typeof(IOptionsFactory<>), typeof(TracingOptionsFactory<>)));

// Что библиотека зарегистрировала в DI (до Build, пока коллекцию можно читать)
var registrations = services
    .Where(d => d.ServiceType.IsGenericTypeDefinition
             && d.ServiceType.Namespace == "Microsoft.Extensions.Options")
    .Select(d => $"{d.ServiceType.Name} -> {d.ImplementationType?.Name} [{d.Lifetime}]")
    .ToList();


builder.Services.AddOpenApi();

var app = builder.Build();


if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// ---------- 3. Версия библиотеки для README и слайда ----------
var asm = typeof(IOptions<>).Assembly;
DemoLog.Write($"Библиотека: {asm.GetName().Name} {asm.GetName().Version}, " +
              $"informational: {asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");

// ---------- 4. Подписка на изменения ----------
// Монитор подписывается на токены в конструкторе. Пока его никто не запросил, подписок нет.
var monitor = app.Services.GetRequiredService<IOptionsMonitor<ApiOptions>>();
monitor.OnChange((o, name) =>
    DemoLog.Write($"OnChange слушатель: name='{name}', Limit={o.Limit}, Title='{o.Title}'"));

// ---------- 5. Эндпоинты ----------

// Что и с каким временем жизни зарегистрировано
app.MapGet("/demo/registrations", () => registrations);

// Три способа получить настройки. Смотрите InstanceId и трассу в консоли.
app.MapGet("/demo/compare", (IOptions<ApiOptions> opt,
                             IOptionsSnapshot<ApiOptions> snap,
                             IOptionsMonitor<ApiOptions> mon) =>
{
    DemoLog.Write("--- GET /demo/compare ---");
    return new[]
    {
        OptionsView.From("IOptions.Value", opt.Value),
        OptionsView.From("IOptionsSnapshot.Value (1-е чтение)", snap.Value),
        OptionsView.From("IOptionsSnapshot.Value (2-е чтение)", snap.Value),
        OptionsView.From("IOptionsMonitor.CurrentValue", mon.CurrentValue),
    };
});

// Именованные опции: /demo/named/billing, /demo/named/other, /demo/named/broken
app.MapGet("/demo/named/{name}", (string name, IOptionsMonitor<ApiOptions> mon) =>
{
    DemoLog.Write($"--- GET /demo/named/{name} ---");
    try
    {
        return Results.Ok(OptionsView.From($"IOptionsMonitor.Get('{name}')", mon.Get(name)));
    }
    catch (OptionsValidationException ex)
    {
        return Results.UnprocessableEntity(new { name = ex.OptionsName, failures = ex.Failures });
    }
});

// Запасной способ вызвать перезагрузку без правки файла.
// Опирается на поведение индексатора конфигурации (см. раздел «Что проверить»).
// Изменяет значение через провайдер, который вызывает OnReload()
app.MapPost("/demo/limit/{value:int}", (int value) =>
{
    DemoLog.Write($"--- POST /demo/limit/{value} ---");

    // Не даём значениям, которые не пройдут валидацию: исключение полетит из обработчика токена,
    // а как именно оно обрабатывается, мы пока не проверяли.
    if (value <= 0)
        return Results.BadRequest("Limit must be positive");

    manualConfig.Provider.SetAndReload("Api:Limit", value.ToString());
    return Results.Ok($"Api:Limit = {value}");
});

// Срабатывание токена без изменения значений
app.MapPost("/demo/reload", () =>
{
    manualToken.Trigger();
    return Results.Ok("token triggered");
});

app.Run();

/*
var builder = WebApplication.CreateBuilder(args);
*/

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
/*
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
*/

/*var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};*/

/*app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");*/

/*
app.Run();
*/

/*record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}*/


public static class DemoLog
{
    // Номер потока нужен, чтобы показать: перезагрузка идёт не в потоке запроса
    public static void Write(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} T{Environment.CurrentManagedThreadId,-3} {message}");
};


public class ApiOptions
{
    public string Title { get; set; } = "";
    public int Limit { get; set; }
}

public record OptionsView(string Source, int InstanceId, int Limit, string Title)
{
    // InstanceId показывает, один это объект или разные
    public static OptionsView From(string source, ApiOptions o) =>
        new(source, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o), o.Limit, o.Title);
}

// Фаза 1. IConfigureNamedOptions получает имя при любом обращении.
// Ничего не меняет, только показывает состояние после всех предыдущих конфигураторов.
public sealed class TraceNamedConfigure : IConfigureNamedOptions<ApiOptions>
{
    public void Configure(string? name, ApiOptions o) =>
        DemoLog.Write($"  [Configure #6] IConfigureNamedOptions, name='{name}', состояние: Limit={o.Limit}, Title='{o.Title}'");

    // Обычный интерфейс. Вызывается фабрикой только для имени по умолчанию,
    // но здесь реализация одна на оба случая.
    public void Configure(ApiOptions o) => Configure(Options.DefaultName, o);
}

// Фаза 2. Выполняется после всех Configure, независимо от порядка регистрации.
public sealed class TracePostConfigure : IPostConfigureOptions<ApiOptions>
{
    public void PostConfigure(string? name, ApiOptions o)
    {
        DemoLog.Write($"  [PostConfigure] name='{name}': обрезаю пробелы в Title '{o.Title}'");
        o.Title = o.Title.Trim();
    }
}

// Фаза 3. Валидация. Ошибки копятся, потом фабрика бросает OptionsValidationException.
public sealed class LimitValidator : IValidateOptions<ApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiOptions o)
    {
        DemoLog.Write($"  [Validate] name='{name}': Limit={o.Limit}");
        return o.Limit > 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Limit must be positive, got {o.Limit}");
    }
}


// Декоратор над настоящей OptionsFactory. Подменяет IOptionsFactory<> одной регистрацией в DI.
// Ни IOptions, ни IOptionsSnapshot, ни IOptionsMonitor об этом не знают.
public sealed class TracingOptionsFactory<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>
    : IOptionsFactory<TOptions> where TOptions : class
{
    private readonly OptionsFactory<TOptions> _inner;

    public TracingOptionsFactory(OptionsFactory<TOptions> inner) => _inner = inner;

    public TOptions Create(string name)
    {
        if (typeof(TOptions) != typeof(ApiOptions))
            return _inner.Create(name);
        
        DemoLog.Write($"IOptionsFactory.Create(name='{name}') НАЧАЛО [{typeof(TOptions).Name}]");
        try
        {
            TOptions result = _inner.Create(name);
            DemoLog.Write($"IOptionsFactory.Create(name='{name}') КОНЕЦ, экземпляр #{RuntimeHelpers.GetHashCode(result)}");
            return result;
        }
        catch (Exception ex)
        {
            DemoLog.Write($"IOptionsFactory.Create(name='{name}') ОШИБКА: {ex.GetType().Name}");
            throw;
        }
    }
}

// Провайдер конфигурации, который умеет менять значение И сообщать об этом.
// Именно OnReload() заставляет сработать цепочку токенов.
public sealed class ManualConfigurationProvider : ConfigurationProvider
{
    public void SetAndReload(string key, string value)
    {
        DemoLog.Write($"  [Provider] Data['{key}'] = '{value}'");
        Data[key] = value;
        DemoLog.Write("  [Provider] OnReload(): срабатывает токен провайдера");
        OnReload();
    }
}

public sealed class ManualConfigurationSource : IConfigurationSource
{
    public ManualConfigurationProvider Provider { get; } = new();

    // Возвращаем один и тот же экземпляр, чтобы эндпоинт мог до него дотянуться
    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

// Источник токена в обход Configuration: показывает контракт IOptionsChangeTokenSource в чистом виде
// и одноразовость токена.
public sealed class ManualChangeTokenSource<TOptions> : IOptionsChangeTokenSource<TOptions>
{
    private CancellationTokenSource _cts = new();

    public string Name => Options.DefaultName;

    public IChangeToken GetChangeToken()
    {
        DemoLog.Write("  [TokenSource] GetChangeToken() вызван");
        return new CancellationChangeToken(_cts.Token);
    }

    public void Trigger()
    {
        DemoLog.Write("--- TokenSource.Trigger(): подменяю токен, отменяю старый ---");
        // Сначала новый токен, потом отмена старого: токен одноразовый
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old.Cancel();
    }
}