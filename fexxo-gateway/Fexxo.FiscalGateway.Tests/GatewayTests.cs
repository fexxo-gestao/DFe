using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using ServicoNfse = Unimake.Business.DFe.Servicos.NFSe.ServicoBase;

namespace Fexxo.FiscalGateway.Tests;

public sealed class TransmissorSimulado : ITransmissorNfseNacional
{
    public string? Resposta { get; set; }
    public Dictionary<string, string> RespostaPorServico { get; } = new();
    public Exception? Falha { get; set; }
    public int Chamadas { get; private set; }

    public void Reiniciar()
    {
        Resposta = null;
        Falha = null;
        Chamadas = 0;
        RespostaPorServico.Clear();
    }

    public void Transmitir(ServicoNfse servico)
    {
        Chamadas++;
        if (Falha is not null)
        {
            throw Falha;
        }
        var resposta = RespostaPorServico.GetValueOrDefault(servico.GetType().Name) ?? Resposta;
        if (resposta is null)
        {
            return;
        }
        var xml = new XmlDocument();
        xml.LoadXml(resposta);
        servico.RetornoWSString = resposta;
        servico.RetornoWSXML = xml;
    }
}

public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    public const string Token = "token-de-teste-com-mais-de-32-caracteres";
    public TransmissorSimulado Transmissor { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("FISCAL_GATEWAY_TOKEN", Token);
        builder.ConfigureServices(servicos =>
        {
            servicos.RemoveAll<ITransmissorNfseNacional>();
            servicos.AddSingleton<ITransmissorNfseNacional>(Transmissor);
        });
    }
}

public sealed class GatewayTests : IClassFixture<GatewayFactory>
{
    private const string NamespaceNfse = "http://www.sped.fazenda.gov.br/nfse";
    private readonly GatewayFactory fabrica;

    public GatewayTests(GatewayFactory fabrica)
    {
        this.fabrica = fabrica;
        fabrica.Transmissor.Reiniciar();
    }

    [Fact]
    public async Task Healthz_RespondeSemToken()
    {
        var resposta = await fabrica.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task RotasFiscais_RecusamTokenAusenteOuErrado()
    {
        var cliente = fabrica.CreateClient();
        var semToken = await cliente.PostAsJsonAsync("/v1/certificado/inspecionar", new { });
        cliente.DefaultRequestHeaders.Add("X-Fiscal-Gateway-Token", "outro-token-com-mais-de-trinta-e-dois-chars");
        var tokenErrado = await cliente.PostAsJsonAsync("/v1/certificado/inspecionar", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, semToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tokenErrado.StatusCode);
    }

    [Fact]
    public async Task InspecionarCertificado_DevolveCnpjTitularEValidade()
    {
        var (dto, _) = CertificadoDeTeste("FEXXO BARBEARIA LTDA:12345678000195");

        var resposta = await Cliente().PostAsJsonAsync("/v1/certificado/inspecionar", new InspecionarCertificadoRequest(dto));
        var corpo = await resposta.Content.ReadFromJsonAsync<InspecionarCertificadoResponse>();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal("12345678000195", corpo!.Cnpj);
        Assert.Equal("FEXXO BARBEARIA LTDA:12345678000195", corpo.Titular);
        Assert.True(corpo.ValidoAte > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task InspecionarCertificado_SenhaErradaViraErroDeCertificadoSemVazarDetalhe()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");

        var resposta = await Cliente().PostAsJsonAsync("/v1/certificado/inspecionar", new InspecionarCertificadoRequest(dto with { Senha = "errada" }));
        var problema = await resposta.Content.ReadFromJsonAsync<ProblemaDto>();

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("certificado_invalido", problema!.Codigo);
        Assert.DoesNotContain("errada", problema.Mensagem);
    }

    [Fact]
    public async Task AssinarNfseNacional_ValidaNoXsdEAssinaComOCertificadoDoLojista()
    {
        var (dto, certificado) = CertificadoDeTeste("EMPRESA:12345678000195");

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/assinar", new NfseNacionalRequest("homologacao", dto, DpsSemAssinatura()));
        var corpo = await resposta.Content.ReadFromJsonAsync<AssinarNfseResponse>();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.True(AssinaturaValida(corpo!.DpsAssinadaXml, certificado));
        Assert.Equal(0, fabrica.Transmissor.Chamadas);
    }

    [Fact]
    public async Task EmitirNfseNacional_AutorizadaDevolveChaveNumeroEXml()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");
        fabrica.Transmissor.Resposta = File.ReadAllText(Recurso("nfse-autorizada.xml"));

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/emitir", new NfseNacionalRequest("homologacao", dto, DpsSemAssinatura()));
        var corpo = await resposta.Content.ReadFromJsonAsync<EmitirNfseResponse>();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(StatusEmissao.Autorizada, corpo!.Status);
        Assert.Equal("43149022226263261000198000000000000225120787292537", corpo.ChaveAcesso);
        Assert.Equal("2", corpo.Numero);
        Assert.Contains("<infNFSe", corpo.NfseXml);
        Assert.Empty(corpo.Erros);
    }

    [Fact]
    public async Task EmitirNfseNacional_RejeitadaDevolveCodigoEMensagemDaReceita()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");
        fabrica.Transmissor.Resposta = "<temp><tipoAmbiente>2</tipoAmbiente><versaoAplicativo>SefinNacional_1.6.0</versaoAplicativo><dataHoraProcessamento>2026-09-26T10:00:00-03:00</dataHoraProcessamento><erros><Codigo>E0014</Codigo><Descricao>DPS já utilizada.</Descricao></erros></temp>";

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/emitir", new NfseNacionalRequest("homologacao", dto, DpsSemAssinatura()));
        var corpo = await resposta.Content.ReadFromJsonAsync<EmitirNfseResponse>();

        Assert.Equal(StatusEmissao.Rejeitada, corpo!.Status);
        Assert.Null(corpo.ChaveAcesso);
        Assert.Contains(corpo.Erros, erro => erro.Codigo == "E0014" && erro.Mensagem == "DPS já utilizada.");
    }

    [Fact]
    public async Task EmitirNfseNacional_ReceitaForaDoArViraBadGatewayParaAApiTentarDeNovo()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");
        fabrica.Transmissor.Falha = new ServicoIndisponivelException("O Sistema Nacional da NFS-e não respondeu.");

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/emitir", new NfseNacionalRequest("homologacao", dto, DpsSemAssinatura()));
        var problema = await resposta.Content.ReadFromJsonAsync<ProblemaDto>();

        Assert.Equal(HttpStatusCode.BadGateway, resposta.StatusCode);
        Assert.Equal("servico_indisponivel", problema!.Codigo);
    }

    [Fact]
    public async Task EmitirNfseNacional_DpsForaDoSchemaViraUnprocessableSemTransmitir()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");
        var dpsSemPrestador = DpsSemAssinatura().Replace("<prest>", "<prestx>").Replace("</prest>", "</prestx>");

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/emitir", new NfseNacionalRequest("homologacao", dto, dpsSemPrestador));
        var problema = await resposta.Content.ReadFromJsonAsync<ProblemaDto>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("documento_invalido", problema!.Codigo);
        Assert.Equal(0, fabrica.Transmissor.Chamadas);
    }

    [Fact]
    public async Task Ambiente_DesconhecidoViraBadRequest()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/assinar", new NfseNacionalRequest("teste", dto, DpsSemAssinatura()));
        var problema = await resposta.Content.ReadFromJsonAsync<ProblemaDto>();

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.Equal("ambiente_invalido", problema!.Codigo);
    }

    private HttpClient Cliente()
    {
        var cliente = fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add("X-Fiscal-Gateway-Token", GatewayFactory.Token);
        return cliente;
    }

    private static (CertificadoDto Dto, X509Certificate2 Certificado) CertificadoDeTeste(string nomeComum)
    {
        using var rsa = RSA.Create(2048);
        var requisicao = new CertificateRequest($"CN={nomeComum}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var efemero = requisicao.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = efemero.Export(X509ContentType.Pfx, "senha-do-teste");
        return (new CertificadoDto(Convert.ToBase64String(pfx), "senha-do-teste"), new X509Certificate2(pfx, "senha-do-teste"));
    }

    private static string DpsSemAssinatura()
    {
        var documento = new XmlDocument();
        documento.Load(Recurso("dps-nacional.xml"));
        foreach (var assinatura in documento.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl).Cast<XmlNode>().ToList())
        {
            assinatura.ParentNode!.RemoveChild(assinatura);
        }
        return documento.OuterXml;
    }

    private static string Recurso(string nome) => Path.Combine(AppContext.BaseDirectory, "Recursos", nome);

    private static bool AssinaturaValida(string xml, X509Certificate2 certificado)
    {
        var documento = new XmlDocument { PreserveWhitespace = true };
        documento.LoadXml(xml);
        Assert.Equal(1, documento.GetElementsByTagName("infDPS", NamespaceNfse).Count);
        var assinaturas = documento.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
        Assert.Equal(1, assinaturas.Count);
        var signedXml = new SignedXml(documento);
        signedXml.LoadXml((XmlElement)assinaturas[0]!);
        return signedXml.CheckSignature(certificado, true);
    }

    [Fact]
    public async Task ConsultarDps_EncontraANotaGeradaPelaDpsEDevolveChaveNumeroEXml()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");
        fabrica.Transmissor.RespostaPorServico["ConsultarNfsePorRps"] = "<temp><tipoAmbiente>2</tipoAmbiente><versaoAplicativo>1</versaoAplicativo><dataHoraProcessamento>2026-09-27T10:00:00-03:00</dataHoraProcessamento><chaveAcesso>43149022226263261000198000000000000225120787292537</chaveAcesso></temp>";
        fabrica.Transmissor.RespostaPorServico["ConsultarNfse"] = File.ReadAllText(Recurso("nfse-autorizada.xml"));

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/consultar-dps", new ConsultarDpsRequest("homologacao", dto, "DPS354890621122233300018100001000000000000042"));
        var corpo = await resposta.Content.ReadFromJsonAsync<ConsultarDpsResponse>();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.True(corpo!.Encontrada);
        Assert.Equal("43149022226263261000198000000000000225120787292537", corpo.ChaveAcesso);
        Assert.Equal("2", corpo.Numero);
        Assert.Contains("<infNFSe", corpo.NfseXml);
        Assert.Equal(2, fabrica.Transmissor.Chamadas);
    }

    [Fact]
    public async Task ConsultarDps_DpsSemNotaVoltaNaoEncontradaSemConsultarANota()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");
        fabrica.Transmissor.RespostaPorServico["ConsultarNfsePorRps"] = "<temp><tipoAmbiente>2</tipoAmbiente><versaoAplicativo>1</versaoAplicativo><dataHoraProcessamento>2026-09-27T10:00:00-03:00</dataHoraProcessamento><erro><codigo>E0404</codigo><descricao>DPS não encontrada.</descricao></erro></temp>";

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/consultar-dps", new ConsultarDpsRequest("homologacao", dto, "DPS354890621122233300018100001000000000000042"));
        var corpo = await resposta.Content.ReadFromJsonAsync<ConsultarDpsResponse>();

        Assert.False(corpo!.Encontrada);
        Assert.Equal(1, fabrica.Transmissor.Chamadas);
    }

    [Fact]
    public async Task ConsultarDps_RecusaIdentificadorForaDoFormato()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/consultar-dps", new ConsultarDpsRequest("homologacao", dto, "DPS123"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal(0, fabrica.Transmissor.Chamadas);
    }

    [Fact]
    public async Task RegistrarEvento_CancelamentoAceitoDevolveOEventoAssinadoPelaReceita()
    {
        var (dto, certificado) = CertificadoDeTeste("EMPRESA:12345678000195");
        fabrica.Transmissor.Resposta = File.ReadAllText(Recurso("evento-cancelamento.xml"));

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/evento", new RegistrarEventoRequest("homologacao", dto, PedidoSemAssinatura()));
        var corpo = await resposta.Content.ReadFromJsonAsync<RegistrarEventoResponse>();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(StatusEvento.Registrado, corpo!.Status);
        Assert.Contains("<infEvento", corpo.EventoXml);
        var pedido = new XmlDocument { PreserveWhitespace = true };
        pedido.LoadXml(corpo.PedidoAssinadoXml);
        var signedXml = new SignedXml(pedido);
        signedXml.LoadXml((XmlElement)pedido.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        Assert.True(signedXml.CheckSignature(certificado, true));
    }

    [Fact]
    public async Task RegistrarEvento_RecusaDaReceitaVoltaComCodigoEMensagem()
    {
        var (dto, _) = CertificadoDeTeste("EMPRESA:12345678000195");
        fabrica.Transmissor.Resposta = "<temp><tipoAmbiente>2</tipoAmbiente><versaoAplicativo>1</versaoAplicativo><dataHoraProcessamento>2026-09-27T10:00:00-03:00</dataHoraProcessamento><erro><codigo>E1235</codigo><descricao>Prazo de cancelamento expirado.</descricao></erro></temp>";

        var resposta = await Cliente().PostAsJsonAsync("/v1/nfse/nacional/evento", new RegistrarEventoRequest("homologacao", dto, PedidoSemAssinatura()));
        var corpo = await resposta.Content.ReadFromJsonAsync<RegistrarEventoResponse>();

        Assert.Equal(StatusEvento.Rejeitado, corpo!.Status);
        Assert.Contains(corpo.Erros, erro => erro.Codigo == "E1235" && erro.Mensagem == "Prazo de cancelamento expirado.");
    }

    private static string PedidoSemAssinatura()
    {
        var documento = new XmlDocument();
        documento.Load(Recurso("pedido-cancelamento.xml"));
        return documento.OuterXml;
    }
}
