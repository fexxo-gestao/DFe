using Xunit;

namespace Fexxo.FiscalGateway.Tests;

public sealed class BibliotecaSimulada : IBibliotecaNfse
{
    public Dictionary<string, string> Configuracao { get; } = new();
    public string? Rps { get; private set; }
    public int? Modo { get; private set; }
    public string Resposta { get; set; } = "{}";

    public void GravarConfiguracao(string sessao, string chave, string valor) => Configuracao[$"{sessao}.{chave}"] = valor;

    public void UsarBibliotecasSsl() => Configuracao["DFe.SSLXmlSignLib"] = "1";

    public void CarregarRps(string ini) => Rps = ini;

    public string GerarLote(string lote) => Resposta;

    public string ConsultarNfsePorFaixa(string numeroInicial, string numeroFinal, int pagina) => Resposta;

    public string Emitir(string lote, int modoEnvio)
    {
        Modo = modoEnvio;
        Configuracao["Snapshot.DadosPFX"] = Configuracao.GetValueOrDefault("DFe.DadosPFX", string.Empty);
        Configuracao["Snapshot.WSUser"] = Configuracao.GetValueOrDefault("NFSe.Emitente.WSUser", string.Empty);
        Configuracao["Snapshot.WSSenha"] = Configuracao.GetValueOrDefault("NFSe.Emitente.WSSenha", string.Empty);
        return Resposta;
    }
}

public class NfseMunicipalTests
{
    private const string Servicos = "[3550308]\r\nNome=Sao Paulo\r\nUF=SP\r\nProvedor=ISSSaoPaulo\r\n\r\n[3548906]\r\n; Atualizado\r\nNome=Sao Carlos\r\nUF=SP\r\nProvedor=PadraoNacional\r\n";

    private static NfseMunicipal Criar(BibliotecaSimulada biblioteca) =>
        new(biblioteca, new TabelaMunicipios(TabelaMunicipios.Ler(Servicos)));

    private static EmitirNfseMunicipalRequest Pedido(bool teste = false) => new(
        "homologacao",
        new CertificadoDto("UEZY", "senha"),
        3550308,
        new EmitenteMunicipalDto("66640025000168", "12345678", "FEXXO GESTAO LTDA"),
        "1",
        "[IdentificacaoRps]\nNumero=1",
        teste);

    [Fact]
    public void LeATabelaDeProvedoresDoAcbrPorCodigoIbge()
    {
        var tabela = new TabelaMunicipios(TabelaMunicipios.Ler(Servicos));

        Assert.Equal(new ProvedorMunicipalResponse(3550308, "Sao Paulo", "SP", "ISSSaoPaulo", false), tabela.Buscar(3550308));
        Assert.True(tabela.Buscar(3548906)!.PadraoNacional);
        Assert.Null(tabela.Buscar(9999999));
    }

    [Fact]
    public void ModoTesteDeSaoPauloVaiParaProducaoNoMetodoDeTesteELimpaOCertificadoDepois()
    {
        var biblioteca = new BibliotecaSimulada { Resposta = """{"Envio":{"Sucesso":true,"NumeroNota":"","XmlEnvio":"<x/>"}}""" };

        var resposta = Criar(biblioteca).Emitir(Pedido(teste: true));

        Assert.Equal(StatusNfseMunicipal.Validada, resposta.Status);
        Assert.Equal(4, biblioteca.Modo);
        Assert.Equal("0", biblioteca.Configuracao["NFSe.Ambiente"]);
        Assert.Equal("UEZY", biblioteca.Configuracao["Snapshot.DadosPFX"]);
        Assert.Equal(string.Empty, biblioteca.Configuracao["DFe.DadosPFX"]);
        Assert.Equal(string.Empty, biblioteca.Configuracao["DFe.Senha"]);
    }

    [Fact]
    public void UsuarioESenhaDoWebserviceDaPrefeituraVaoNoEnvioEASenhaEhApagadaDepois()
    {
        var biblioteca = new BibliotecaSimulada { Resposta = """{"Envio":{"Sucesso":true,"NumeroNota":"9"}}""" };
        var pedido = Pedido() with { Emitente = new EmitenteMunicipalDto("66640025000168", "123", "FEXXO", "usuario.prefeitura", "senha-ws") };

        Criar(biblioteca).Emitir(pedido);

        Assert.Equal("usuario.prefeitura", biblioteca.Configuracao["Snapshot.WSUser"]);
        Assert.Equal("senha-ws", biblioteca.Configuracao["Snapshot.WSSenha"]);
        Assert.Equal(string.Empty, biblioteca.Configuracao["NFSe.Emitente.WSSenha"]);
    }

    [Fact]
    public void NotaComNumeroEstaAutorizadaEErrosViramRecusaComCorrecao()
    {
        var autorizada = NfseMunicipal.Interpretar("""{"Envio":{"Sucesso":true,"NumeroNota":"123","CodigoVerificacao":"ABCD1234","Protocolo":"99"}}""", teste: false);
        var recusada = NfseMunicipal.Interpretar("""{"Envio":{"Sucesso":false,"Erro1":{"Codigo":"1057","Descricao":"CCM não pertence ao CNPJ.","Correcao":"Confira a inscrição."}}}""", teste: false);

        Assert.Equal(StatusNfseMunicipal.Autorizada, autorizada.Status);
        Assert.Equal("123", autorizada.Numero);
        Assert.Equal("ABCD1234", autorizada.CodigoVerificacao);
        Assert.Equal(StatusNfseMunicipal.Rejeitada, recusada.Status);
        Assert.Equal(new ErroFiscalDto("1057", "CCM não pertence ao CNPJ. Confira a inscrição."), Assert.Single(recusada.Erros));
    }

    [Fact]
    public void ErroDeConexaoDoAcbrSignificaPrefeituraForaDoAr()
    {
        var erro = Assert.Throws<ServicoIndisponivelException>(() => NfseMunicipal.Interpretar(
            """{"Envio":{"Sucesso":false,"Erro1":{"Codigo":"X999","Descricao":"Erro de Conexão: timeout"}}}""", teste: false));

        Assert.Contains("A prefeitura não respondeu", erro.Message);
    }

    [Fact]
    public void PrefeituraQueRecusaOCertificadoNaConexaoNaoEhForaDoAr()
    {
        var erro = Assert.Throws<CertificadoInvalidoException>(() => NfseMunicipal.Interpretar(
            """{"Envio":{"Sucesso":false,"Erro1":{"Codigo":"X999","Descricao":"Erro de Conexão: Start tag expected"},"XmlRetorno":"You do not have permission to view this directory or page."}}""", teste: true));

        Assert.Contains("recusou o certificado", erro.Message);
    }

    [Fact]
    public void RecusaMunicipioSemProvedorConhecido()
    {
        var pedido = Pedido() with { CodigoMunicipio = 9999999 };

        Assert.Throws<DocumentoInvalidoException>(() => Criar(new BibliotecaSimulada()).Emitir(pedido));
    }
}

public class NfseMunicipalSemHomologacaoTests
{
    [Fact]
    public void CidadeSemHomologacaoRefazNoModoDeTesteDaPrefeitura()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"Envio":{"Sucesso":false,"Erro1":{"Codigo":"X999","Descricao":"Erro de Conexão: Não informado a URL de Homologação, favor entrar em contato com a Prefeitura ou Provedor."}}}""",
            """{"Envio":{"Sucesso":true,"NumeroNota":""}}""");
        var nfse = new NfseMunicipal(biblioteca, new TabelaMunicipios(TabelaMunicipios.Ler("[3550308]\nNome=Sao Paulo\nUF=SP\nProvedor=ISSSaoPaulo\n")));

        var resposta = nfse.Emitir(new EmitirNfseMunicipalRequest("homologacao", new CertificadoDto("UEZY", "s"), 3550308, new EmitenteMunicipalDto("66640025000168", "12345678", "FEXXO"), "1", "[IdentificacaoRps]", false));

        Assert.Equal(StatusNfseMunicipal.Validada, resposta.Status);
        Assert.Equal(new[] { 1, 4 }, biblioteca.Modos);
        Assert.Equal(new[] { "1", "0" }, biblioteca.Ambientes);
    }
}

public sealed class BibliotecaEmSequencia(params string[] respostas) : IBibliotecaNfse
{
    private int indice;
    private string ambiente = string.Empty;
    public List<int> Modos { get; } = new();
    public List<string> Ambientes { get; } = new();

    public void GravarConfiguracao(string sessao, string chave, string valor)
    {
        if (chave == "Ambiente") ambiente = valor;
    }

    public void UsarBibliotecasSsl() { }

    public void CarregarRps(string ini) => Inis.Add(ini);

    public List<string> Inis { get; } = new();

    public string GerarLote(string lote) => respostas[indice++];

    public List<string> Consultas { get; } = new();

    public string ConsultarNfsePorFaixa(string numeroInicial, string numeroFinal, int pagina)
    {
        Consultas.Add(ambiente);
        return respostas[indice++];
    }

    public string Emitir(string lote, int modoEnvio)
    {
        Modos.Add(modoEnvio);
        Ambientes.Add(ambiente);
        return respostas[indice++];
    }
}

public class NfseMunicipalAjustesDeLayoutTests
{
    private const string Ini = "[Tomador]\nCNPJCPF=52998224725\n\n[Servico]\nItemListaServico=06.01\nCodigoServicoNacional=060101\nCodigoNBS=126021000\ncClassTrib=000001\nDiscriminacao=Corte\n\n[IBSCBSDPS]\nfinNFSe=0\n\n[gIBSCBS]\nCST=000\n\n[Valores]\nValorServicos=1.00";

    private static NfseMunicipal Criar(IBibliotecaNfse biblioteca) =>
        new(biblioteca, new TabelaMunicipios(TabelaMunicipios.Ler("[1100023]\nNome=Ariquemes\nUF=RO\nProvedor=Fiorilli\n")));

    private static EmitirNfseMunicipalRequest Pedido() =>
        new("homologacao", new CertificadoDto("UEZY", "s"), 1100023, new EmitenteMunicipalDto("66640025000168", "123", "FEXXO"), "1", Ini, false);

    [Fact]
    public void LayoutAntigoQueRecusaCamposDaReformaRecebeANotaSemEles()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Erro de Validação: Element '{http://www.abrasf.org.br/nfse.xsd}CodigoServicoNacional': This element is not expected."}}}""",
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Erro de Validação: Element 'cNBS': This element is not expected."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(Pedido());

        Assert.True(resposta.Valida);
        Assert.Equal(new[] { "sem_codigo_nacional", "sem_nbs" }, resposta.Ajustes);
        var terceira = biblioteca.Inis[2];
        Assert.DoesNotContain("CodigoServicoNacional", terceira);
        Assert.DoesNotContain("CodigoNBS", terceira);
        Assert.Contains("[gIBSCBS]", terceira);
        Assert.Contains("cClassTrib=000001", terceira);
        Assert.Contains("ItemListaServico=06.01", terceira);
    }

    [Fact]
    public void LayoutQueUsaOCodigoNacionalComoItemRecebeOsSeisDigitos()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element '{http://www.abrasf.org.br/nfse.xsd}cTribNac': [facet 'pattern'] The value '06.01' is not accepted by the pattern '[0-9]{6}'."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(Pedido());

        Assert.Equal(new[] { "item_com_codigo_nacional" }, resposta.Ajustes);
        Assert.Contains("ItemListaServico=060101", biblioteca.Inis[1]);
    }

    [Fact]
    public void LayoutQueExigeTipoDeRecolhimentoEResponsavelPelaRetencaoRecebeOsDois()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element '{http://www.ctaconsult.com/nfse}tipoRecolhimento': [facet 'enumeration'] The value '' is not an element of the set {'1', '2', '3'}."}}}""",
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element '{https://x}ResponsavelRetencao': '' is not a valid value of the atomic type."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(Pedido() with { RpsIni = "[IdentificacaoRps]\nNumero=1\n\n[Servico]\nDiscriminacao=Corte" });

        Assert.Equal(new[] { "recolhimento_pelo_prestador", "responsavel_retencao_tomador" }, resposta.Ajustes);
        Assert.Contains("[IdentificacaoRps]\nTipoRecolhimento=1\nNumero=1", biblioteca.Inis[2]);
        Assert.Contains("[Servico]\nResponsavelRetencao=1\nDiscriminacao=Corte", biblioteca.Inis[2]);
    }

    [Fact]
    public void LayoutQueExigeEnderecoDoClienteSaiSemClienteIdentificado()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'Tomador': Missing child element(s). Expected is ( Endereco )."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(Pedido());

        Assert.True(resposta.Valida);
        Assert.Equal(new[] { "sem_tomador" }, resposta.Ajustes);
        Assert.DoesNotContain("[Tomador]", biblioteca.Inis[1]);
    }

    [Fact]
    public void MensagemDeErroEmTextoDaBibliotecaViraRecusaLegivel()
    {
        var biblioteca = new BibliotecaEmSequencia("Lote não pode ser gerado: informe o regime.");

        var resposta = Criar(biblioteca).Validar(Pedido());

        Assert.False(resposta.Valida);
        Assert.Equal(new ErroFiscalDto("acbr", "Lote não pode ser gerado: informe o regime."), Assert.Single(resposta.Erros));
        Assert.Equal(StatusNfseMunicipal.Rejeitada, NfseMunicipal.Interpretar("Falha qualquer", teste: false).Status);
    }

    [Fact]
    public void ErroDeLayoutSemAjusteConhecidoVoltaComoRecusa()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'Aliquota': value too long."}}}""");

        var resposta = Criar(biblioteca).Validar(Pedido());

        Assert.False(resposta.Valida);
        Assert.Empty(resposta.Ajustes);
        Assert.Single(resposta.Erros);
    }

    private static EmitirNfseMunicipalRequest ComIni(string ini) => Pedido() with { RpsIni = ini };

    private const string ComPrestador = "[Prestador]\nCNPJ=66640025000168\nLogradouro=Rua Teste\nNumero=10\nBairro=Centro\nCEP=13560000\nCodigoMunicipio=3548906\nUF=SP\nDDD=16\nTelefone=33719900\n\n[Tomador]\nCNPJCPF=52998224725\nRazaoSocial=Cliente\nDDD=16\nTelefone=991397711\nEmail=c@x.com\n\n[Servico]\nCodigoServicoNacional=060101\nDiscriminacao=Corte";

    [Fact]
    public void TomadorSemEnderecoRecebeOEnderecoDoEstabelecimentoAntesDeSairDaNota()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'nrCep': [facet 'pattern'] The value '' is not accepted by the pattern '[0-9]{8}'."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(ComIni(ComPrestador));

        Assert.Equal(new[] { "tomador_com_endereco_do_estabelecimento" }, resposta.Ajustes);
        var tomador = biblioteca.Inis[1][biblioteca.Inis[1].IndexOf("[Tomador]", StringComparison.Ordinal)..];
        Assert.Contains("CEP=13560000", tomador);
        Assert.Contains("UF=SP", tomador);
        Assert.Contains("CodigoMunicipio=3548906", tomador);
        Assert.Contains("CNPJCPF=52998224725", tomador);
    }

    [Fact]
    public void QuandoOEnderecoNaoBastaANotaSaiSemTomador()
    {
        var recusa = """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'Endereco': Missing child element(s). Expected is ( Cep )."}}}""";
        var biblioteca = new BibliotecaEmSequencia(recusa, recusa, """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(ComIni(ComPrestador));

        Assert.Equal(new[] { "tomador_com_endereco_do_estabelecimento", "sem_tomador" }, resposta.Ajustes);
        Assert.DoesNotContain("[Tomador]", biblioteca.Inis[2]);
    }

    [Fact]
    public void DddDeTresDigitosTelefoneLongoEPaisAntesDoContato()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'Ddd': [facet 'minLength'] The value has a length of '2'; this underruns the allowed minimum length of '3'."}}}""",
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'Telefone': [facet 'maxLength'] The value has a length of '9'; this exceeds the allowed maximum length of '8'."}}}""",
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'Email': This element is not expected. Expected is ( Pais )."}}}""",
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'Endereco': Missing child element(s). Expected is ( Pais )."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(ComIni(ComPrestador));

        Assert.Equal(new[] { "ddd_com_zero", "sem_telefone", "sem_email", "com_pais" }, resposta.Ajustes);
        Assert.Equal(2, biblioteca.Inis[4].Split("xPais=BRASIL").Length - 1);
        Assert.Contains("DDD=016", biblioteca.Inis[1]);
        Assert.DoesNotContain("Telefone=", biblioteca.Inis[2]);
        Assert.DoesNotContain("Email=", biblioteca.Inis[3]);
    }

    [Fact]
    public void AbrasfV1RecebeOCodigoNacionalNaChaveQueEleLeEPisCofinsSemIncidencia()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'Discriminacao': This element is not expected. Expected is ( {http://www.abrasf.org.br/nfse.xsd}CodigoServicoNacional )."}}}""",
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'PisCofinsCst': [facet 'enumeration'] The value '' is not an element of the set {'00'}."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(ComIni(ComPrestador));

        Assert.Equal(new[] { "com_ctribnac", "pis_cofins_sem_incidencia" }, resposta.Ajustes);
        Assert.Contains("cTribNac=060101", biblioteca.Inis[1]);
        Assert.Contains("[tribFederal]\nCST=00", biblioteca.Inis[2]);
    }

    [Fact]
    public void ProvedorQueNumeraANotaEhConsultadoAntesDeEmitir()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"Envio":{"Erro1":{"Codigo":"999","Descricao":"Número da NFSe não informado. Utilize o ConsultarNFSePorFaixa para receber o próximo número do provedor."}}}""",
            """{"ConsultaNFSe":{"Sucesso":true,"NumeroNota":"1234"}}""",
            """{"Envio":{"Sucesso":true,"NumeroNota":"1234","CodigoVerificacao":"ABC","XmlEnvio":"<x/>"}}""");

        var resposta = Criar(biblioteca).Emitir(ComIni(ComPrestador));

        Assert.Equal(new[] { "numero_do_provedor" }, resposta.Ajustes);
        Assert.Single(biblioteca.Consultas);
        Assert.Contains("[IdentificacaoNFSe]\nNumero=1234", biblioteca.Inis[1]);
    }

    [Fact]
    public void ValidacaoDeProvedorQueNumeraUsaNumeroProvisorioSemConsultar()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"999","Descricao":"Utilize o ConsultarNFSePorFaixa para receber o próximo número do provedor."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(ComIni(ComPrestador));

        Assert.True(resposta.Valida);
        Assert.Empty(biblioteca.Consultas);
        Assert.Contains("Numero=1", biblioteca.Inis[1][biblioteca.Inis[1].IndexOf("[IdentificacaoNFSe]", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void PrefeituraQueConsultaOCodigoNoFormatoNacionalCompletoRecebeODesdobro()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"999","Descricao":"Codigo nao encontrado na consulta. Codigo=06.01, URL=http://x"}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(ComIni(ComPrestador.Replace("[Servico]\n", "[Servico]\nItemListaServico=06.01\n", StringComparison.Ordinal)));

        Assert.Equal(new[] { "item_com_desdobro_nacional" }, resposta.Ajustes);
        Assert.Contains("ItemListaServico=06.01.01.000", biblioteca.Inis[1]);
    }

    [Fact]
    public void UfDaPrestacaoCnaeDeNoveDigitosEPisCofinsNaoRetido()
    {
        var biblioteca = new BibliotecaEmSequencia(
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'codigoEstado': [facet 'enumeration'] The value '' is not an element of the set {'SP'}."}}}""",
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'codigoAtividade': [facet 'pattern'] The value '6203100' is not accepted by the pattern '[0-9]{9}'."}}}""",
            """{"GerarLote":{"Erro1":{"Codigo":"X800","Descricao":"Element 'TpRetPisCofins': '' is not a valid value of the atomic type 'tsRetPisCofins'."}}}""",
            """{"GerarLote":{"XmlEnvio":"<Lote/>"}}""");

        var resposta = Criar(biblioteca).Validar(ComIni(ComPrestador.Replace("[Servico]\n", "[Servico]\nCodigoCnae=6203100\n", StringComparison.Ordinal)));

        Assert.Equal(new[] { "com_uf_da_prestacao", "cnae_com_nove_digitos", "pis_cofins_nao_retido" }, resposta.Ajustes);
        Assert.Contains("UFPrestacao=SP", biblioteca.Inis[1]);
        Assert.Contains("CodigoCnae=620310000", biblioteca.Inis[2]);
        Assert.Contains("tpRetPisCofins=2", biblioteca.Inis[3]);
    }
}
