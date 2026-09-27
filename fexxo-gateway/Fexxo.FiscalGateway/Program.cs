using System.Security.Cryptography;
using System.Text;
using Fexxo.FiscalGateway;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("FISCAL_GATEWAY_URLS") ?? "http://127.0.0.1:8090");
builder.Services.AddSingleton<ITransmissorFiscal, TransmissorUnimake>();
builder.Services.AddSingleton<NfseNacional>();
builder.Services.AddSingleton<NfceSefaz>();
builder.Services.AddSingleton(new TokenDoGateway(builder.Configuration["FISCAL_GATEWAY_TOKEN"]));

var app = builder.Build();

app.UseExceptionHandler(erros => erros.Run(async contexto =>
{
    var erro = contexto.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, problema) = Problemas.Mapear(erro);
    if (status >= 500)
    {
        app.Logger.LogError("fiscal_gateway_falha tipo={Tipo}", erro?.GetType().Name);
    }
    contexto.Response.StatusCode = status;
    await contexto.Response.WriteAsJsonAsync(problema);
}));

app.Use(async (contexto, proximo) =>
{
    if (contexto.Request.Path.StartsWithSegments("/healthz"))
    {
        await proximo(contexto);
        return;
    }

    var token = contexto.RequestServices.GetRequiredService<TokenDoGateway>();
    if (!token.Aceita(contexto.Request.Headers["X-Fiscal-Gateway-Token"]))
    {
        contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await contexto.Response.WriteAsJsonAsync(new ProblemaDto("nao_autorizado", "Token do gateway fiscal inválido."));
        return;
    }

    await proximo(contexto);
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/v1/certificado/inspecionar", (InspecionarCertificadoRequest requisicao) =>
{
    using var certificado = Certificados.Carregar(requisicao.Certificado);
    return Results.Ok(Certificados.Inspecionar(certificado));
});

app.MapPost("/v1/nfse/nacional/assinar", (NfseNacionalRequest requisicao, NfseNacional nfse) => Results.Ok(nfse.Assinar(requisicao)));

app.MapPost("/v1/nfse/nacional/emitir", (NfseNacionalRequest requisicao, NfseNacional nfse) => Results.Ok(nfse.Emitir(requisicao)));

app.MapPost("/v1/nfse/nacional/consultar-dps", (ConsultarDpsRequest requisicao, NfseNacional nfse) => Results.Ok(nfse.ConsultarDps(requisicao)));

app.MapPost("/v1/nfse/nacional/evento", (RegistrarEventoRequest requisicao, NfseNacional nfse) => Results.Ok(nfse.RegistrarEvento(requisicao)));

app.MapPost("/v1/nfce/autorizar", (AutorizarNfceRequest requisicao, NfceSefaz nfce) => Results.Ok(nfce.Autorizar(requisicao)));

app.MapPost("/v1/nfce/transmitir-contingencia", (TransmitirContingenciaNfceRequest requisicao, NfceSefaz nfce) => Results.Ok(nfce.TransmitirContingencia(requisicao)));

app.MapPost("/v1/nfce/consultar", (ConsultarNfceRequest requisicao, NfceSefaz nfce) => Results.Ok(nfce.Consultar(requisicao)));

app.MapPost("/v1/nfce/status", (StatusNfceRequest requisicao, NfceSefaz nfce) => Results.Ok(nfce.Status(requisicao)));

app.MapPost("/v1/nfce/inutilizar", (PedidoNfceRequest requisicao, NfceSefaz nfce) => Results.Ok(nfce.Inutilizar(requisicao)));

app.MapPost("/v1/nfce/evento", (PedidoNfceRequest requisicao, NfceSefaz nfce) => Results.Ok(nfce.RegistrarEvento(requisicao)));

app.Run();

public sealed class TokenDoGateway
{
    private readonly byte[] esperado;

    public TokenDoGateway(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
        {
            throw new InvalidOperationException("FISCAL_GATEWAY_TOKEN precisa ter pelo menos 32 caracteres.");
        }
        esperado = Encoding.UTF8.GetBytes(token);
    }

    public bool Aceita(string? recebido) =>
        recebido is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(recebido), esperado);
}

public static class Problemas
{
    public static (int Status, ProblemaDto Problema) Mapear(Exception? erro) => erro switch
    {
        CertificadoInvalidoException e => (StatusCodes.Status400BadRequest, new ProblemaDto("certificado_invalido", e.Message)),
        AmbienteInvalidoException e => (StatusCodes.Status400BadRequest, new ProblemaDto("ambiente_invalido", e.Message)),
        DocumentoInvalidoException e => (StatusCodes.Status422UnprocessableEntity, new ProblemaDto("documento_invalido", e.Message)),
        BadHttpRequestException => (StatusCodes.Status400BadRequest, new ProblemaDto("requisicao_invalida", "Corpo da requisição inválido.")),
        ServicoIndisponivelException e => (StatusCodes.Status502BadGateway, new ProblemaDto("servico_indisponivel", e.Message)),
        _ => (StatusCodes.Status500InternalServerError, new ProblemaDto("erro_interno", "Falha inesperada no gateway fiscal.")),
    };
}

public partial class Program;
