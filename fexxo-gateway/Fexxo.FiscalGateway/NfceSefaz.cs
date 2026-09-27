using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using Unimake.Business.DFe.Servicos;
using Unimake.Exceptions;
using AutorizacaoNfce = Unimake.Business.DFe.Servicos.NFCe.Autorizacao;
using ConsultaProtocoloNfce = Unimake.Business.DFe.Servicos.NFCe.ConsultaProtocolo;
using InutilizacaoNfce = Unimake.Business.DFe.Servicos.NFCe.Inutilizacao;
using RecepcaoEventoNfce = Unimake.Business.DFe.Servicos.NFCe.RecepcaoEvento;
using StatusServicoNfce = Unimake.Business.DFe.Servicos.NFCe.StatusServico;

namespace Fexxo.FiscalGateway;

public sealed class NfceSefaz(ITransmissorFiscal transmissor)
{
    public const string NamespaceNfe = "http://www.portalfiscal.inf.br/nfe";

    private static readonly HashSet<string> Autorizadas = ["100", "150"];
    private static readonly HashSet<string> Duplicadas = ["204", "539"];
    private static readonly HashSet<string> ServicoFora = ["108", "109"];
    private static readonly HashSet<string> EventosRegistrados = ["135", "136", "155"];

    public AutorizarNfceResponse Autorizar(AutorizarNfceRequest requisicao)
    {
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var emissao = requisicao.Contingencia ? TipoEmissao.ContingenciaOffLine : TipoEmissao.Normal;
        var servico = PrepararAutorizacao(requisicao.NfeXml, requisicao.Ambiente, requisicao.Csc, certificado, emissao);
        var nfeAssinada = NfeAssinada(servico);
        var qrCode = Texto(nfeAssinada, "qrCode");

        if (requisicao.Contingencia)
        {
            return new AutorizarNfceResponse(StatusNfce.ContingenciaAssinada, nfeAssinada.OuterXml, ChaveDaNfe(nfeAssinada), null, null, qrCode, []);
        }

        return Transmitir(servico, nfeAssinada, qrCode);
    }

    public AutorizarNfceResponse TransmitirContingencia(TransmitirContingenciaNfceRequest requisicao)
    {
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var original = NfseNacional.CarregarXml(requisicao.NfeAssinadaXml);
        if (original.GetElementsByTagName("Signature").Count == 0 || original.GetElementsByTagName("infNFeSupl").Count == 0)
        {
            throw new DocumentoInvalidoException("A NFC-e de contingência precisa chegar assinada e com o QR Code gerado na emissão offline.");
        }
        var servico = PrepararAutorizacao(requisicao.NfeAssinadaXml, requisicao.Ambiente, requisicao.Csc, certificado, TipoEmissao.ContingenciaOffLine);
        var nfeAssinada = NfeAssinada(servico);
        return Transmitir(servico, nfeAssinada, Texto(nfeAssinada, "qrCode"));
    }

    public ConsultarNfceResponse Consultar(ConsultarNfceRequest requisicao)
    {
        if (requisicao.Chave.Length != 44 || !requisicao.Chave.All(char.IsAsciiLetterOrDigit))
        {
            throw new DocumentoInvalidoException("A chave da NFC-e deve ter 44 caracteres.");
        }
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var pedido = $"<consSitNFe versao=\"4.00\" xmlns=\"{NamespaceNfe}\"><tpAmb>{TpAmb(requisicao.Ambiente)}</tpAmb><xServ>CONSULTAR</xServ><chNFe>{requisicao.Chave}</chNFe></consSitNFe>";
        var servico = Criar(() => new ConsultaProtocoloNfce(pedido, Configurar(requisicao.Ambiente, certificado, null, TipoEmissao.Normal)));
        transmissor.Transmitir(servico);
        var retorno = Retorno(servico.RetornoWSXML);

        var cStat = Texto(retorno.DocumentElement!, "cStat") ?? "";
        var protocolo = retorno.GetElementsByTagName("protNFe").OfType<XmlElement>().FirstOrDefault();
        var situacao = cStat switch
        {
            "100" or "150" => SituacaoNfce.Autorizada,
            "101" or "151" or "155" => SituacaoNfce.Cancelada,
            "110" or "301" or "302" or "303" => SituacaoNfce.Denegada,
            "217" => SituacaoNfce.NaoEncontrada,
            _ when ServicoFora.Contains(cStat) => throw new ServicoIndisponivelException("A SEFAZ está fora do ar."),
            _ => SituacaoNfce.Desconhecida,
        };
        return new ConsultarNfceResponse(
            situacao,
            protocolo is null ? null : Texto(protocolo, "nProt"),
            protocolo?.OuterXml,
            situacao == SituacaoNfce.Desconhecida ? [Erro(retorno.DocumentElement!)] : []);
    }

    public StatusNfceResponse Status(StatusNfceRequest requisicao)
    {
        if (!CodigosUf.TryGetValue(requisicao.Uf.ToUpperInvariant(), out var cUf))
        {
            throw new DocumentoInvalidoException($"UF '{requisicao.Uf}' inválida.");
        }
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var pedido = $"<consStatServ versao=\"4.00\" xmlns=\"{NamespaceNfe}\"><tpAmb>{TpAmb(requisicao.Ambiente)}</tpAmb><cUF>{cUf}</cUF><xServ>STATUS</xServ></consStatServ>";
        var servico = Criar(() => new StatusServicoNfce(pedido, Configurar(requisicao.Ambiente, certificado, null, TipoEmissao.Normal)));
        transmissor.Transmitir(servico);
        var retorno = Retorno(servico.RetornoWSXML).DocumentElement!;
        var cStat = Texto(retorno, "cStat") ?? "";
        return new StatusNfceResponse(cStat == "107", cStat, Texto(retorno, "xMotivo") ?? "");
    }

    public InutilizarNfceResponse Inutilizar(PedidoNfceRequest requisicao)
    {
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var servico = Criar(() => new InutilizacaoNfce(requisicao.PedidoXml, Configurar(requisicao.Ambiente, certificado, null, TipoEmissao.Normal)));
        var pedidoAssinado = servico.ConteudoXMLAssinado.OuterXml;
        transmissor.Transmitir(servico);
        var retorno = Retorno(servico.RetornoWSXML);
        var infInut = retorno.GetElementsByTagName("infInut").OfType<XmlElement>().FirstOrDefault() ?? retorno.DocumentElement!;
        var cStat = Texto(infInut, "cStat") ?? "";
        if (ServicoFora.Contains(cStat))
        {
            throw new ServicoIndisponivelException("A SEFAZ está fora do ar.");
        }
        return cStat == "102"
            ? new InutilizarNfceResponse(StatusEvento.Registrado, Texto(infInut, "nProt"), ProcInutilizacao(pedidoAssinado, servico.RetornoWSString), [])
            : new InutilizarNfceResponse(StatusEvento.Rejeitado, null, null, [Erro(infInut)]);
    }

    public RegistrarEventoResponse RegistrarEvento(PedidoNfceRequest requisicao)
    {
        using var certificado = Certificados.Carregar(requisicao.Certificado);
        var servico = Criar(() => new RecepcaoEventoNfce(requisicao.PedidoXml, Configurar(requisicao.Ambiente, certificado, null, TipoEmissao.Normal)));
        var pedidoAssinado = servico.ConteudoXMLAssinado;
        transmissor.Transmitir(servico);
        var retorno = Retorno(servico.RetornoWSXML);
        var retEvento = retorno.GetElementsByTagName("retEvento").OfType<XmlElement>().FirstOrDefault();
        var alvo = retEvento ?? retorno.DocumentElement!;
        var cStat = Texto(alvo, "cStat") ?? "";
        if (ServicoFora.Contains(Texto(retorno.DocumentElement!, "cStat") ?? ""))
        {
            throw new ServicoIndisponivelException("A SEFAZ está fora do ar.");
        }
        if (retEvento is null || !EventosRegistrados.Contains(cStat))
        {
            return new RegistrarEventoResponse(StatusEvento.Rejeitado, pedidoAssinado.OuterXml, null, [Erro(alvo)]);
        }
        var evento = pedidoAssinado.GetElementsByTagName("evento").OfType<XmlElement>().First();
        var procEvento = $"<procEventoNFe versao=\"1.00\" xmlns=\"{NamespaceNfe}\">{evento.OuterXml}{retEvento.OuterXml}</procEventoNFe>";
        return new RegistrarEventoResponse(StatusEvento.Registrado, pedidoAssinado.OuterXml, procEvento, []);
    }

    private AutorizarNfceResponse Transmitir(AutorizacaoNfce servico, XmlElement nfeAssinada, string? qrCode)
    {
        transmissor.Transmitir(servico);
        var retorno = Retorno(servico.RetornoWSXML);
        var lote = retorno.DocumentElement!;
        var cStatLote = Texto(lote, "cStat") ?? "";
        if (ServicoFora.Contains(cStatLote))
        {
            throw new ServicoIndisponivelException("A SEFAZ está fora do ar.");
        }

        var protNFe = retorno.GetElementsByTagName("protNFe").OfType<XmlElement>().FirstOrDefault();
        var infProt = protNFe?.GetElementsByTagName("infProt").OfType<XmlElement>().FirstOrDefault();
        var cStat = infProt is null ? cStatLote : Texto(infProt, "cStat") ?? "";
        var chave = ChaveDaNfe(nfeAssinada);

        if (infProt is not null && Autorizadas.Contains(cStat))
        {
            var nfeProc = $"<nfeProc versao=\"4.00\" xmlns=\"{NamespaceNfe}\">{nfeAssinada.OuterXml}{protNFe!.OuterXml}</nfeProc>";
            return new AutorizarNfceResponse(StatusNfce.Autorizada, nfeAssinada.OuterXml, chave, Texto(infProt, "nProt"), nfeProc, qrCode, []);
        }
        if (Duplicadas.Contains(cStat))
        {
            return new AutorizarNfceResponse(StatusNfce.Duplicada, nfeAssinada.OuterXml, chave, null, null, qrCode, [Erro(infProt ?? lote)]);
        }
        return new AutorizarNfceResponse(StatusNfce.Rejeitada, nfeAssinada.OuterXml, chave, null, null, qrCode, [Erro(infProt ?? lote)]);
    }

    private static AutorizacaoNfce PrepararAutorizacao(string nfeXml, string ambiente, CscDto? csc, X509Certificate2 certificado, TipoEmissao emissao)
    {
        var nfe = NfseNacional.CarregarXml(nfeXml).DocumentElement;
        if (nfe is null || nfe.LocalName != "NFe" || nfe.NamespaceURI != NamespaceNfe)
        {
            throw new DocumentoInvalidoException($"Esperado o elemento <NFe xmlns=\"{NamespaceNfe}\">.");
        }
        var lote = $"<enviNFe versao=\"4.00\" xmlns=\"{NamespaceNfe}\"><idLote>{IdLote()}</idLote><indSinc>1</indSinc>{nfe.OuterXml}</enviNFe>";
        return Criar(() => new AutorizacaoNfce(lote, Configurar(ambiente, certificado, csc, emissao)));
    }

    private static T Criar<T>(Func<T> construir)
    {
        try
        {
            return construir();
        }
        catch (ValidarXMLException erro)
        {
            throw new DocumentoInvalidoException(erro.Message);
        }
        catch (XmlException erro)
        {
            throw new DocumentoInvalidoException($"XML malformado: {erro.Message}");
        }
    }

    private static Configuracao Configurar(string ambiente, X509Certificate2 certificado, CscDto? csc, TipoEmissao emissao) => new()
    {
        TipoDFe = TipoDFe.NFCe,
        TipoAmbiente = NfseNacional.Ambiente(ambiente),
        TipoEmissao = emissao,
        CertificadoDigital = certificado,
        CSC = csc?.Valor ?? "",
        CSCIDToken = csc?.Id ?? 0,
    };

    private static XmlElement NfeAssinada(AutorizacaoNfce servico) =>
        servico.ConteudoXMLAssinado.GetElementsByTagName("NFe").OfType<XmlElement>().FirstOrDefault()
        ?? throw new DocumentoInvalidoException("A NFC-e não foi montada para assinatura.");

    private static string? ChaveDaNfe(XmlElement nfe)
    {
        var id = nfe.GetElementsByTagName("infNFe").OfType<XmlElement>().FirstOrDefault()?.GetAttribute("Id");
        return string.IsNullOrEmpty(id) ? null : id.StartsWith("NFe", StringComparison.Ordinal) ? id[3..] : id;
    }

    private static XmlDocument Retorno(XmlDocument? retorno) =>
        retorno?.DocumentElement is null ? throw new ServicoIndisponivelException("A SEFAZ respondeu sem conteúdo.") : retorno;

    private static string? Texto(XmlNode raiz, string tag) =>
        raiz is XmlElement elemento
            ? elemento.GetElementsByTagName(tag).OfType<XmlElement>().FirstOrDefault()?.InnerText.Trim()
            : null;

    private static ErroFiscalDto Erro(XmlNode alvo) =>
        new(Texto(alvo, "cStat") ?? "desconhecido", Texto(alvo, "xMotivo") ?? "A SEFAZ recusou sem informar o motivo.");

    private static string ProcInutilizacao(string pedidoAssinado, string retorno)
    {
        var pedido = NfseNacional.CarregarXml(pedidoAssinado).DocumentElement!.OuterXml;
        var ret = NfseNacional.CarregarXml(retorno).GetElementsByTagName("retInutNFe").OfType<XmlElement>().FirstOrDefault()?.OuterXml ?? retorno;
        return $"<ProcInutNFe versao=\"4.00\" xmlns=\"{NamespaceNfe}\">{pedido}{ret}</ProcInutNFe>";
    }

    private static string TpAmb(string ambiente) => NfseNacional.Ambiente(ambiente) == TipoAmbiente.Producao ? "1" : "2";

    private static string IdLote() => RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString("D15");

    private static readonly Dictionary<string, string> CodigosUf = new()
    {
        ["RO"] = "11", ["AC"] = "12", ["AM"] = "13", ["RR"] = "14", ["PA"] = "15", ["AP"] = "16", ["TO"] = "17",
        ["MA"] = "21", ["PI"] = "22", ["CE"] = "23", ["RN"] = "24", ["PB"] = "25", ["PE"] = "26", ["AL"] = "27", ["SE"] = "28", ["BA"] = "29",
        ["MG"] = "31", ["ES"] = "32", ["RJ"] = "33", ["SP"] = "35",
        ["PR"] = "41", ["SC"] = "42", ["RS"] = "43",
        ["MS"] = "50", ["MT"] = "51", ["GO"] = "52", ["DF"] = "53",
    };
}
