using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using Xunit;

namespace Fexxo.FiscalGateway.Tests;

public sealed class NfceTests : IClassFixture<GatewayFactory>
{
    private const string Ns = NfceSefaz.NamespaceNfe;
    private const string ChaveNormal = "35260911222333000181650010000000421430517502";
    private static readonly CscDto Csc = new(1, "0123456789ABCDEF0123456789ABCDEF0123");
    private readonly GatewayFactory fabrica;

    public NfceTests(GatewayFactory fabrica)
    {
        this.fabrica = fabrica;
        fabrica.Transmissor.Reiniciar();
    }

    [Fact]
    public async Task Autorizar_ValidaNoXsdAssinaGeraQrCodeEMontaONfeProc()
    {
        var (dto, certificado) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        fabrica.Transmissor.Resposta = RetornoAutorizacao(ChaveNormal, "100", "Autorizado o uso da NF-e");

        var resposta = await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/autorizar", new AutorizarNfceRequest("homologacao", dto, Csc, Xml("nfce-fexxo.xml"), false));
        Assert.True(resposta.IsSuccessStatusCode, await resposta.Content.ReadAsStringAsync());
        var corpo = await resposta.Content.ReadFromJsonAsync<AutorizarNfceResponse>();

        Assert.Equal(StatusNfce.Autorizada, corpo!.Status);
        Assert.Equal(ChaveNormal, corpo.ChaveAcesso);
        Assert.Equal("135260000000001", corpo.Protocolo);
        Assert.StartsWith("https://", corpo.QrCodeUrl);
        Assert.Contains(ChaveNormal, corpo.QrCodeUrl);
        Assert.True(AssinaturaValida(corpo.NfeAssinadaXml, certificado));
        var proc = Carregar(corpo.NfeProcXml!);
        Assert.Equal("nfeProc", proc.DocumentElement!.LocalName);
        Assert.Equal(1, proc.GetElementsByTagName("NFe", Ns).Count);
        Assert.Equal(1, proc.GetElementsByTagName("infNFeSupl", Ns).Count);
        Assert.Equal("100", proc.GetElementsByTagName("cStat", Ns)[0]!.InnerText);
        Assert.Equal(1, fabrica.Transmissor.Chamadas);
    }

    [Fact]
    public async Task Autorizar_EmContingenciaAssinaComQrCodeSemTransmitir()
    {
        var (dto, certificado) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");

        var resposta = await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/autorizar", new AutorizarNfceRequest("homologacao", dto, Csc, Xml("nfce-fexxo-contingencia.xml"), true));
        var corpo = await resposta.Content.ReadFromJsonAsync<AutorizarNfceResponse>();

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(StatusNfce.ContingenciaAssinada, corpo!.Status);
        Assert.Equal(0, fabrica.Transmissor.Chamadas);
        Assert.Null(corpo.NfeProcXml);
        var nfe = Carregar(corpo.NfeAssinadaXml);
        Assert.Equal("9", nfe.GetElementsByTagName("tpEmis", Ns)[0]!.InnerText);
        Assert.Equal(1, nfe.GetElementsByTagName("infNFeSupl", Ns).Count);
        Assert.True(AssinaturaValida(corpo.NfeAssinadaXml, certificado));
    }

    [Fact]
    public async Task TransmitirContingencia_EnviaAMesmaNotaSemReassinar()
    {
        var (dto, _) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        var cliente = Apoio.Cliente(fabrica);
        var offline = await (await cliente.PostAsJsonAsync("/v1/nfce/autorizar", new AutorizarNfceRequest("homologacao", dto, Csc, Xml("nfce-fexxo-contingencia.xml"), true)))
            .Content.ReadFromJsonAsync<AutorizarNfceResponse>();
        fabrica.Transmissor.Resposta = RetornoAutorizacao(offline!.ChaveAcesso!, "100", "Autorizado o uso da NF-e");

        var resposta = await cliente.PostAsJsonAsync("/v1/nfce/transmitir-contingencia", new TransmitirContingenciaNfceRequest("homologacao", dto, Csc, offline.NfeAssinadaXml));
        var corpo = await resposta.Content.ReadFromJsonAsync<AutorizarNfceResponse>();

        Assert.Equal(StatusNfce.Autorizada, corpo!.Status);
        Assert.Equal(offline.ChaveAcesso, corpo.ChaveAcesso);
        Assert.Equal(ValorAssinatura(offline.NfeAssinadaXml), ValorAssinatura(corpo.NfeAssinadaXml));
        Assert.Equal(offline.QrCodeUrl, corpo.QrCodeUrl);
    }

    [Fact]
    public async Task TransmitirContingencia_RecusaNotaQueNaoFoiAssinadaOffline()
    {
        var (dto, _) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");

        var resposta = await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/transmitir-contingencia", new TransmitirContingenciaNfceRequest("homologacao", dto, Csc, Xml("nfce-fexxo-contingencia.xml")));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal(0, fabrica.Transmissor.Chamadas);
    }

    [Theory]
    [InlineData("225", "Rejeicao: Falha no Schema XML", StatusNfce.Rejeitada)]
    [InlineData("204", "Rejeicao: Duplicidade de NF-e", StatusNfce.Duplicada)]
    public async Task Autorizar_DevolveRejeicaoEDuplicidadeComOCodigoDaSefaz(string cStat, string motivo, string esperado)
    {
        var (dto, _) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        fabrica.Transmissor.Resposta = RetornoAutorizacao(ChaveNormal, cStat, motivo);

        var corpo = await (await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/autorizar", new AutorizarNfceRequest("homologacao", dto, Csc, Xml("nfce-fexxo.xml"), false)))
            .Content.ReadFromJsonAsync<AutorizarNfceResponse>();

        Assert.Equal(esperado, corpo!.Status);
        Assert.Null(corpo.NfeProcXml);
        Assert.Equal(new ErroFiscalDto(cStat, motivo), Assert.Single(corpo.Erros));
    }

    [Fact]
    public async Task Autorizar_SefazParalisadaViraServicoIndisponivelParaAApiEntrarEmContingencia()
    {
        var (dto, _) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        fabrica.Transmissor.Resposta = $"<retEnviNFe versao=\"4.00\" xmlns=\"{Ns}\"><tpAmb>2</tpAmb><verAplic>SP</verAplic><cStat>108</cStat><xMotivo>Servico Paralisado Momentaneamente</xMotivo><cUF>35</cUF></retEnviNFe>";

        var resposta = await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/autorizar", new AutorizarNfceRequest("homologacao", dto, Csc, Xml("nfce-fexxo.xml"), false));
        var problema = await resposta.Content.ReadFromJsonAsync<ProblemaDto>();

        Assert.Equal(HttpStatusCode.BadGateway, resposta.StatusCode);
        Assert.Equal("servico_indisponivel", problema!.Codigo);
    }

    [Fact]
    public async Task Autorizar_XmlForaDoSchemaNaoETransmitido()
    {
        var (dto, _) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        var semNcm = Xml("nfce-fexxo.xml").Replace("<NCM>33059000</NCM>", "");

        var resposta = await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/autorizar", new AutorizarNfceRequest("homologacao", dto, Csc, semNcm, false));
        var problema = await resposta.Content.ReadFromJsonAsync<ProblemaDto>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal("documento_invalido", problema!.Codigo);
        Assert.Equal(0, fabrica.Transmissor.Chamadas);
    }

    [Theory]
    [InlineData("100", SituacaoNfce.Autorizada)]
    [InlineData("101", SituacaoNfce.Cancelada)]
    [InlineData("217", SituacaoNfce.NaoEncontrada)]
    public async Task Consultar_TraduzASituacaoDaChave(string cStat, string situacao)
    {
        var (dto, _) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        var protocolo = cStat == "217" ? "" : $"<protNFe versao=\"4.00\"><infProt><tpAmb>2</tpAmb><verAplic>SP</verAplic><chNFe>{ChaveNormal}</chNFe><dhRecbto>2026-09-27T15:30:05-03:00</dhRecbto><nProt>135260000000001</nProt><digVal>AAAA</digVal><cStat>100</cStat><xMotivo>Autorizado o uso da NF-e</xMotivo></infProt></protNFe>";
        fabrica.Transmissor.Resposta = $"<retConsSitNFe versao=\"4.00\" xmlns=\"{Ns}\"><tpAmb>2</tpAmb><verAplic>SP</verAplic><cStat>{cStat}</cStat><xMotivo>ok</xMotivo><cUF>35</cUF><dhRecbto>2026-09-27T15:31:00-03:00</dhRecbto><chNFe>{ChaveNormal}</chNFe>{protocolo}</retConsSitNFe>";

        var corpo = await (await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/consultar", new ConsultarNfceRequest("homologacao", dto, ChaveNormal)))
            .Content.ReadFromJsonAsync<ConsultarNfceResponse>();

        Assert.Equal(situacao, corpo!.Situacao);
        Assert.Equal(cStat == "217" ? null : "135260000000001", corpo.Protocolo);
    }

    [Fact]
    public async Task Status_InformaSeASefazEstaEmOperacao()
    {
        var (dto, _) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        fabrica.Transmissor.Resposta = $"<retConsStatServ versao=\"4.00\" xmlns=\"{Ns}\"><tpAmb>2</tpAmb><verAplic>SP</verAplic><cStat>107</cStat><xMotivo>Servico em Operacao</xMotivo><cUF>35</cUF><dhRecbto>2026-09-27T15:31:00-03:00</dhRecbto></retConsStatServ>";

        var corpo = await (await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/status", new StatusNfceRequest("homologacao", dto, "sp")))
            .Content.ReadFromJsonAsync<StatusNfceResponse>();

        Assert.True(corpo!.EmOperacao);
        Assert.Equal("107", corpo.CStat);
    }

    [Fact]
    public async Task Inutilizar_AssinaTransmiteEDevolveOProcesso()
    {
        var (dto, certificado) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        const string id = "ID35261122233300018165001000000043000000043";
        var pedido = $"<inutNFe versao=\"4.00\" xmlns=\"{Ns}\"><infInut Id=\"{id}\"><tpAmb>2</tpAmb><xServ>INUTILIZAR</xServ><cUF>35</cUF><ano>26</ano><CNPJ>11222333000181</CNPJ><mod>65</mod><serie>1</serie><nNFIni>43</nNFIni><nNFFin>43</nNFFin><xJust>Numero pulado por falha na emissao da venda</xJust></infInut></inutNFe>";
        fabrica.Transmissor.Resposta = $"<retInutNFe versao=\"4.00\" xmlns=\"{Ns}\"><infInut><tpAmb>2</tpAmb><verAplic>SP</verAplic><cStat>102</cStat><xMotivo>Inutilizacao de numero homologado</xMotivo><cUF>35</cUF><ano>26</ano><CNPJ>11222333000181</CNPJ><mod>65</mod><serie>1</serie><nNFIni>43</nNFIni><nNFFin>43</nNFFin><dhRecbto>2026-09-27T15:31:00-03:00</dhRecbto><nProt>135260000000009</nProt></infInut></retInutNFe>";

        var corpo = await (await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/inutilizar", new PedidoNfceRequest("homologacao", dto, pedido)))
            .Content.ReadFromJsonAsync<InutilizarNfceResponse>();

        Assert.Equal(StatusEvento.Registrado, corpo!.Status);
        Assert.Equal("135260000000009", corpo.Protocolo);
        var proc = Carregar(corpo.ProcInutilizacaoXml!);
        Assert.Equal(1, proc.GetElementsByTagName("inutNFe", Ns).Count);
        Assert.Equal(1, proc.GetElementsByTagName("retInutNFe", Ns).Count);
        Assert.Equal(1, proc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl).Count);
    }

    [Fact]
    public async Task Evento_CancelamentoRegistradoDevolveProcEvento()
    {
        var (dto, _) = Apoio.Certificado("BARBEARIA FISCAL LTDA:11222333000181");
        var pedido = $"<envEvento versao=\"1.00\" xmlns=\"{Ns}\"><idLote>1</idLote><evento versao=\"1.00\"><infEvento Id=\"ID110111{ChaveNormal}01\"><cOrgao>35</cOrgao><tpAmb>2</tpAmb><CNPJ>11222333000181</CNPJ><chNFe>{ChaveNormal}</chNFe><dhEvento>2026-09-27T15:40:00-03:00</dhEvento><tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento><verEvento>1.00</verEvento><detEvento versao=\"1.00\"><descEvento>Cancelamento</descEvento><nProt>135260000000001</nProt><xJust>Venda estornada no sistema de gestao</xJust></detEvento></infEvento></evento></envEvento>";
        fabrica.Transmissor.Resposta = $"<retEnvEvento versao=\"1.00\" xmlns=\"{Ns}\"><idLote>1</idLote><tpAmb>2</tpAmb><verAplic>SP</verAplic><cOrgao>35</cOrgao><cStat>128</cStat><xMotivo>Lote de Evento Processado</xMotivo><retEvento versao=\"1.00\"><infEvento><tpAmb>2</tpAmb><verAplic>SP</verAplic><cOrgao>35</cOrgao><cStat>135</cStat><xMotivo>Evento registrado e vinculado a NF-e</xMotivo><chNFe>{ChaveNormal}</chNFe><tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento><dhRegEvento>2026-09-27T15:40:02-03:00</dhRegEvento><nProt>135260000000010</nProt></infEvento></retEvento></retEnvEvento>";

        var corpo = await (await Apoio.Cliente(fabrica).PostAsJsonAsync("/v1/nfce/evento", new PedidoNfceRequest("homologacao", dto, pedido)))
            .Content.ReadFromJsonAsync<RegistrarEventoResponse>();

        Assert.Equal(StatusEvento.Registrado, corpo!.Status);
        var proc = Carregar(corpo.EventoXml!);
        Assert.Equal("procEventoNFe", proc.DocumentElement!.LocalName);
        Assert.Equal(1, proc.GetElementsByTagName("retEvento", Ns).Count);
        Assert.Equal(1, proc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl).Count);
    }

    private static string Xml(string nome) => File.ReadAllText(Apoio.Recurso(nome));

    private static XmlDocument Carregar(string xml)
    {
        var documento = new XmlDocument { PreserveWhitespace = true };
        documento.LoadXml(xml);
        return documento;
    }

    private static string ValorAssinatura(string xml) =>
        Carregar(xml).GetElementsByTagName("SignatureValue", SignedXml.XmlDsigNamespaceUrl)[0]!.InnerText;

    private static bool AssinaturaValida(string xml, X509Certificate2 certificado)
    {
        var documento = Carregar(xml);
        var assinatura = (XmlElement)documento.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!;
        var signedXml = new SignedXml(documento);
        signedXml.LoadXml(assinatura);
        return signedXml.CheckSignature(certificado, true);
    }

    private static string RetornoAutorizacao(string chave, string cStat, string motivo) =>
        $"<retEnviNFe versao=\"4.00\" xmlns=\"{Ns}\"><tpAmb>2</tpAmb><verAplic>SP_NFCE_PL_009_V400</verAplic><cStat>104</cStat><xMotivo>Lote processado</xMotivo><cUF>35</cUF><dhRecbto>2026-09-27T15:30:05-03:00</dhRecbto>"
        + $"<protNFe versao=\"4.00\"><infProt><tpAmb>2</tpAmb><verAplic>SP_NFCE_PL_009_V400</verAplic><chNFe>{chave}</chNFe><dhRecbto>2026-09-27T15:30:05-03:00</dhRecbto>"
        + (cStat is "100" or "150" ? "<nProt>135260000000001</nProt>" : "")
        + $"<digVal>AAAA</digVal><cStat>{cStat}</cStat><xMotivo>{motivo}</xMotivo></infProt></protNFe></retEnviNFe>";
}
