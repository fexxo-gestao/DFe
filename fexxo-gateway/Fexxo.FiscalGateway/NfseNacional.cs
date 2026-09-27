using System.Net;
using System.Xml;
using Unimake.Business.DFe.Servicos;
using Unimake.Exceptions;
using CancelarNfse = Unimake.Business.DFe.Servicos.NFSe.CancelarNfse;
using ConsultarNfse = Unimake.Business.DFe.Servicos.NFSe.ConsultarNfse;
using ConsultarNfsePorRps = Unimake.Business.DFe.Servicos.NFSe.ConsultarNfsePorRps;
using GerarNfse = Unimake.Business.DFe.Servicos.NFSe.GerarNfse;
using ServicoFiscal = Unimake.Business.DFe.Servicos.ServicoBase;

namespace Fexxo.FiscalGateway;

public sealed class DocumentoInvalidoException(string mensagem) : Exception(mensagem);

public sealed class AmbienteInvalidoException(string ambiente) : Exception($"Ambiente '{ambiente}' inválido. Use 'homologacao' ou 'producao'.");

public sealed class ServicoIndisponivelException(string mensagem, Exception? causa = null) : Exception(mensagem, causa);

public interface ITransmissorFiscal
{
    void Transmitir(ServicoFiscal servico);
}

public sealed class TransmissorUnimake : ITransmissorFiscal
{
    public void Transmitir(ServicoFiscal servico)
    {
        try
        {
            servico.Executar();
        }
        catch (Exception erro) when (erro is HttpRequestException or TaskCanceledException or WebException or IOException)
        {
            throw new ServicoIndisponivelException("O serviço da Receita não respondeu.", erro);
        }
    }
}

public sealed partial class NfseNacional(ITransmissorFiscal transmissor)
{
    private const int CodigoPadraoNacional = 1001058;
    private const string PrefixoIdNfse = "NFS";

    public AssinarNfseResponse Assinar(NfseNacionalRequest requisicao)
    {
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var servico = Preparar(requisicao, certificado);
        return new AssinarNfseResponse(servico.ConteudoXMLAssinado.OuterXml);
    }

    public EmitirNfseResponse Emitir(NfseNacionalRequest requisicao)
    {
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var servico = Preparar(requisicao, certificado);
        var dpsAssinada = servico.ConteudoXMLAssinado.OuterXml;

        transmissor.Transmitir(servico);

        if (string.IsNullOrWhiteSpace(servico.RetornoWSString))
        {
            throw new ServicoIndisponivelException("O Sistema Nacional da NFS-e respondeu sem conteúdo.");
        }

        var nfse = servico.Result;
        if (nfse?.InfNFSe is not null)
        {
            return new EmitirNfseResponse(
                StatusEmissao.Autorizada,
                dpsAssinada,
                ChaveDoId(nfse.InfNFSe.Id),
                nfse.InfNFSe.NNFSe,
                servico.RetornoWSString,
                []);
        }

        return new EmitirNfseResponse(StatusEmissao.Rejeitada, dpsAssinada, null, null, null, ErrosDoRetorno(servico));
    }

    public ConsultarDpsResponse ConsultarDps(ConsultarDpsRequest requisicao)
    {
        if (!IdDps().IsMatch(requisicao.IdDps))
        {
            throw new DocumentoInvalidoException("O identificador da DPS deve ter o formato DPS seguido de 42 dígitos.");
        }

        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var porDps = new ConsultarNfsePorRps(
            CarregarXml($"<DPS versao=\"1.01\" xmlns=\"http://www.sped.fazenda.gov.br/nfse\"><infDPS Id=\"{requisicao.IdDps}\"/></DPS>"),
            Configurar(requisicao.Ambiente, certificado, Servico.NFSeConsultarNfsePorRps));
        transmissor.Transmitir(porDps);

        var chave = porDps.Result?.ChaveAcesso;
        if (string.IsNullOrWhiteSpace(chave))
        {
            var erro = porDps.Result?.Erro;
            return new ConsultarDpsResponse(false, null, null, null,
                erro is null || string.IsNullOrWhiteSpace(erro.Codigo) ? [] : [new ErroFiscalDto(erro.Codigo, erro.Descricao ?? "")]);
        }

        var nfse = new ConsultarNfse(
            CarregarXml($"<NFSe versao=\"1.01\" xmlns=\"http://www.sped.fazenda.gov.br/nfse\"><infNFSe Id=\"{PrefixoIdNfse}{chave}\"/></NFSe>"),
            Configurar(requisicao.Ambiente, certificado, Servico.NFSeConsultarNfse));
        transmissor.Transmitir(nfse);

        return new ConsultarDpsResponse(true, chave, nfse.Result?.InfNFSe?.NNFSe, nfse.RetornoWSString, []);
    }

    public RegistrarEventoResponse RegistrarEvento(RegistrarEventoRequest requisicao)
    {
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        CancelarNfse servico;
        try
        {
            servico = new CancelarNfse(CarregarXml(requisicao.PedidoXml), Configurar(requisicao.Ambiente, certificado, Servico.NFSeCancelarNfse));
        }
        catch (ValidarXMLException erro)
        {
            throw new DocumentoInvalidoException(erro.Message);
        }
        var pedidoAssinado = servico.ConteudoXMLAssinado.OuterXml;

        transmissor.Transmitir(servico);
        if (string.IsNullOrWhiteSpace(servico.RetornoWSString))
        {
            throw new ServicoIndisponivelException("O Sistema Nacional da NFS-e respondeu sem conteúdo.");
        }

        if (servico.RetornoWSXML?.DocumentElement?.LocalName == "evento")
        {
            return new RegistrarEventoResponse(StatusEvento.Registrado, pedidoAssinado, servico.RetornoWSString, []);
        }
        return new RegistrarEventoResponse(StatusEvento.Rejeitado, pedidoAssinado, null, ErrosGenericos(servico.RetornoWSXML));
    }

    private static IReadOnlyList<ErroFiscalDto> ErrosGenericos(XmlDocument? retorno)
    {
        var erros = new List<ErroFiscalDto>();
        if (retorno?.DocumentElement is null)
        {
            return erros;
        }
        foreach (XmlElement no in retorno.DocumentElement.GetElementsByTagName("*").OfType<XmlElement>())
        {
            var codigo = no.ChildNodes.OfType<XmlElement>().FirstOrDefault(filho => filho.LocalName.Equals("codigo", StringComparison.OrdinalIgnoreCase))?.InnerText;
            if (string.IsNullOrWhiteSpace(codigo))
            {
                continue;
            }
            var descricao = no.ChildNodes.OfType<XmlElement>().FirstOrDefault(filho => filho.LocalName.Equals("descricao", StringComparison.OrdinalIgnoreCase))?.InnerText ?? "";
            erros.Add(new ErroFiscalDto(codigo.Trim(), descricao.Trim()));
        }
        return erros.Count > 0 ? erros.DistinctBy(erro => erro.Codigo + erro.Mensagem).ToList() : [new ErroFiscalDto("desconhecido", "O Sistema Nacional recusou o evento sem informar o motivo.")];
    }

    private static Configuracao Configurar(string ambiente, System.Security.Cryptography.X509Certificates.X509Certificate2 certificado, Servico servico) => new()
    {
        TipoDFe = TipoDFe.NFSe,
        PadraoNFSe = PadraoNFSe.NACIONAL,
        CodigoMunicipio = CodigoPadraoNacional,
        TipoAmbiente = Ambiente(ambiente),
        Servico = servico,
        SchemaVersao = "1.01",
        CertificadoDigital = certificado,
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"^DPS\d{42}$")]
    private static partial System.Text.RegularExpressions.Regex IdDps();

    public static TipoAmbiente Ambiente(string ambiente) => ambiente switch
    {
        "homologacao" => TipoAmbiente.Homologacao,
        "producao" => TipoAmbiente.Producao,
        _ => throw new AmbienteInvalidoException(ambiente),
    };

    private static GerarNfse Preparar(NfseNacionalRequest requisicao, System.Security.Cryptography.X509Certificates.X509Certificate2 certificado)
    {
        var configuracao = Configurar(requisicao.Ambiente, certificado, Servico.NFSeGerarNfse);

        try
        {
            return new GerarNfse(CarregarXml(requisicao.DpsXml), configuracao);
        }
        catch (ValidarXMLException erro)
        {
            throw new DocumentoInvalidoException(erro.Message);
        }
    }

    internal static XmlDocument CarregarXml(string conteudo)
    {
        var documento = new XmlDocument();
        try
        {
            documento.LoadXml(conteudo);
        }
        catch (XmlException erro)
        {
            throw new DocumentoInvalidoException($"XML malformado: {erro.Message}");
        }
        return documento;
    }

    private static string ChaveDoId(string id) =>
        id.StartsWith(PrefixoIdNfse, StringComparison.Ordinal) ? id[PrefixoIdNfse.Length..] : id;

    private static IReadOnlyList<ErroFiscalDto> ErrosDoRetorno(GerarNfse servico)
    {
        var retorno = servico.ResultErro;
        var erros = new[]
            {
                ErroFiscal(retorno?.Erro?.Codigo, retorno?.Erro?.Descricao, retorno?.Erro?.Complemento),
                ErroFiscal(retorno?.Erros?.Codigo, retorno?.Erros?.Descricao, retorno?.Erros?.Complemento),
            }
            .OfType<ErroFiscalDto>()
            .DistinctBy(erro => erro.Codigo + erro.Mensagem)
            .ToList();

        return erros.Count > 0 ? erros : [new ErroFiscalDto("desconhecido", "O Sistema Nacional recusou a DPS sem informar o motivo.")];
    }

    private static ErroFiscalDto? ErroFiscal(string? codigo, string? descricao, string? complemento) =>
        string.IsNullOrWhiteSpace(codigo)
            ? null
            : new ErroFiscalDto(codigo, string.Join(" ", new[] { descricao, complemento }.Where(parte => !string.IsNullOrWhiteSpace(parte))));
}
