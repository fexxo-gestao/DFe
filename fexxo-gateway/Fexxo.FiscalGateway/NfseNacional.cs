using System.Net;
using System.Xml;
using Unimake.Business.DFe.Servicos;
using Unimake.Exceptions;
using GerarNfse = Unimake.Business.DFe.Servicos.NFSe.GerarNfse;

namespace Fexxo.FiscalGateway;

public sealed class DocumentoInvalidoException(string mensagem) : Exception(mensagem);

public sealed class AmbienteInvalidoException(string ambiente) : Exception($"Ambiente '{ambiente}' inválido. Use 'homologacao' ou 'producao'.");

public sealed class ServicoIndisponivelException(string mensagem, Exception? causa = null) : Exception(mensagem, causa);

public interface ITransmissorNfseNacional
{
    void Transmitir(GerarNfse servico);
}

public sealed class TransmissorUnimake : ITransmissorNfseNacional
{
    public void Transmitir(GerarNfse servico)
    {
        try
        {
            servico.Executar();
        }
        catch (Exception erro) when (erro is HttpRequestException or TaskCanceledException or WebException or IOException)
        {
            throw new ServicoIndisponivelException("O Sistema Nacional da NFS-e não respondeu.", erro);
        }
    }
}

public sealed class NfseNacional(ITransmissorNfseNacional transmissor)
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

    public static TipoAmbiente Ambiente(string ambiente) => ambiente switch
    {
        "homologacao" => TipoAmbiente.Homologacao,
        "producao" => TipoAmbiente.Producao,
        _ => throw new AmbienteInvalidoException(ambiente),
    };

    private static GerarNfse Preparar(NfseNacionalRequest requisicao, System.Security.Cryptography.X509Certificates.X509Certificate2 certificado)
    {
        var configuracao = new Configuracao
        {
            TipoDFe = TipoDFe.NFSe,
            PadraoNFSe = PadraoNFSe.NACIONAL,
            CodigoMunicipio = CodigoPadraoNacional,
            TipoAmbiente = Ambiente(requisicao.Ambiente),
            Servico = Servico.NFSeGerarNfse,
            SchemaVersao = "1.01",
            CertificadoDigital = certificado,
        };

        try
        {
            return new GerarNfse(CarregarXml(requisicao.DpsXml), configuracao);
        }
        catch (ValidarXMLException erro)
        {
            throw new DocumentoInvalidoException(erro.Message);
        }
    }

    private static XmlDocument CarregarXml(string conteudo)
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
