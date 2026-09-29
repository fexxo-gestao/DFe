using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fexxo.FiscalGateway;

public sealed record EmitenteMunicipalDto(
    string Cnpj,
    string? InscricaoMunicipal,
    string RazaoSocial,
    string? UsuarioWebservice = null,
    string? SenhaWebservice = null,
    string? ChaveAcessoWebservice = null,
    string? ChaveAutorizacaoWebservice = null,
    string? CnpjPrefeitura = null);

public sealed record EmitirNfseMunicipalRequest(
    string Ambiente,
    CertificadoDto Certificado,
    int CodigoMunicipio,
    EmitenteMunicipalDto Emitente,
    string Lote,
    string RpsIni,
    bool Teste);

public static class StatusNfseMunicipal
{
    public const string Autorizada = "authorized";
    public const string Rejeitada = "rejected";
    public const string Validada = "validated";
    public const string EmProcessamento = "processing";
}

public sealed record ValidarNfseMunicipalResponse(bool Valida, string? XmlEnvio, IReadOnlyList<ErroFiscalDto> Erros, IReadOnlyList<string> Ajustes);

public sealed record EmitirNfseMunicipalResponse(
    string Status,
    string? Numero,
    string? CodigoVerificacao,
    string? Protocolo,
    string? Link,
    string? XmlEnvio,
    string? XmlRetorno,
    IReadOnlyList<ErroFiscalDto> Erros,
    IReadOnlyList<string>? Ajustes = null);

public sealed record ProvedorMunicipalResponse(int CodigoMunicipio, string Nome, string Uf, string Provedor, bool PadraoNacional);

public interface IBibliotecaNfse
{
    void GravarConfiguracao(string sessao, string chave, string valor);
    void UsarBibliotecasSsl();
    void CarregarRps(string ini);
    string Emitir(string lote, int modoEnvio);
    string GerarLote(string lote, int modoEnvio);
    string ConsultarNfsePorFaixa(string numeroInicial, string numeroFinal, int pagina);
    void DefinirVersaoDoLayout(string versao);
}

internal static partial class AcbrNfseNativo
{
    private const string Biblioteca = "acbrnfse64";

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_Inicializar")]
    internal static partial int Inicializar(byte[] arquivoConfig, byte[] chaveCrypt);

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_ConfigGravarValor")]
    internal static partial int ConfigGravarValor(byte[] sessao, byte[] chave, byte[] valor);

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_LimparLista")]
    internal static partial int LimparLista();

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_CarregarINI")]
    internal static partial int CarregarIni(byte[] ini);

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_Emitir")]
    internal static partial int Emitir(byte[] lote, int modoEnvio, [MarshalAs(UnmanagedType.U1)] bool imprimir, byte[] resposta, ref int tamanho);

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_GerarLote")]
    internal static partial int GerarLote(byte[] lote, int qtdMaximaRps, int modoEnvio, byte[] resposta, ref int tamanho);

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_ConsultarNFSePorFaixa")]
    internal static partial int ConsultarNfsePorFaixa(byte[] numeroInicial, byte[] numeroFinal, int pagina, byte[] resposta, ref int tamanho);

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_SetVersaoDF")]
    internal static partial int DefinirVersaoDoLayout(byte[] versao);

    [LibraryImport(Biblioteca, EntryPoint = "NFSE_UltimoRetorno")]
    internal static partial int UltimoRetorno(byte[] resposta, ref int tamanho);
}

public sealed class BibliotecaAcbr : IBibliotecaNfse
{
    private const int TamanhoInicial = 256 * 1024;
    private static readonly object Trava = new();
    private static bool inicializada;

    public BibliotecaAcbr(string pastaSchemas)
    {
        lock (Trava)
        {
            if (inicializada) return;
            var config = Path.Combine(Path.GetTempPath(), "fexxo-acbrnfse.ini");
            File.WriteAllText(config, string.Empty);
            Verificar(AcbrNfseNativo.Inicializar(Texto(config), Texto(string.Empty)));
            inicializada = true;
            GravarConfiguracao("Principal", "TipoResposta", "2");
            GravarConfiguracao("NFSe", "PathSchemas", pastaSchemas);
            GravarConfiguracao("NFSe", "ExibirErroSchema", "1");
            GravarConfiguracao("NFSe", "ConsultaLoteAposEnvio", "1");
            var pastaTrabalho = Path.Combine(Path.GetTempPath(), "fexxo-acbr");
            Directory.CreateDirectory(pastaTrabalho);
            foreach (var chave in new[] { "SalvarArq", "SalvarGer", "SalvarWS" })
            {
                GravarConfiguracao("NFSe", chave, "0");
            }
            foreach (var chave in new[] { "PathSalvar", "PathGer", "PathRps", "PathNFSe", "PathCan" })
            {
                GravarConfiguracao("NFSe", chave, pastaTrabalho);
            }
        }
    }

    public static object Sincronizacao => Trava;

    public void GravarConfiguracao(string sessao, string chave, string valor) =>
        Verificar(AcbrNfseNativo.ConfigGravarValor(Texto(sessao), Texto(chave), Texto(valor)));

    public void UsarBibliotecasSsl()
    {
        GravarConfiguracao("DFe", "SSLCryptLib", "1");
        GravarConfiguracao("DFe", "SSLHttpLib", "3");
        GravarConfiguracao("DFe", "SSLXmlSignLib", "4");
    }

    public void CarregarRps(string ini)
    {
        Verificar(AcbrNfseNativo.LimparLista());
        Verificar(AcbrNfseNativo.CarregarIni(Texto(ini)));
    }

    public string Emitir(string lote, int modoEnvio)
    {
        var tamanho = TamanhoInicial;
        var resposta = new byte[tamanho];
        Verificar(AcbrNfseNativo.Emitir(Texto(lote), modoEnvio, false, resposta, ref tamanho));
        return tamanho >= resposta.Length ? UltimoRetorno(tamanho) : Decodificar(resposta, tamanho);
    }

    public string GerarLote(string lote, int modoEnvio)
    {
        var tamanho = TamanhoInicial;
        var resposta = new byte[tamanho];
        var codigo = AcbrNfseNativo.GerarLote(Texto(lote), 1, modoEnvio, resposta, ref tamanho);
        if (codigo < 0) return UltimoRetorno(TamanhoInicial);
        return tamanho >= resposta.Length ? UltimoRetorno(tamanho) : Decodificar(resposta, tamanho);
    }

    public void DefinirVersaoDoLayout(string versao) => Verificar(AcbrNfseNativo.DefinirVersaoDoLayout(Texto(versao)));

    public string ConsultarNfsePorFaixa(string numeroInicial, string numeroFinal, int pagina)
    {
        var tamanho = TamanhoInicial;
        var resposta = new byte[tamanho];
        var codigo = AcbrNfseNativo.ConsultarNfsePorFaixa(Texto(numeroInicial), Texto(numeroFinal), pagina, resposta, ref tamanho);
        if (codigo < 0) return UltimoRetorno(TamanhoInicial);
        return tamanho >= resposta.Length ? UltimoRetorno(tamanho) : Decodificar(resposta, tamanho);
    }

    private static string UltimoRetorno(int tamanhoNecessario)
    {
        var tamanho = tamanhoNecessario + 1;
        var resposta = new byte[tamanho];
        AcbrNfseNativo.UltimoRetorno(resposta, ref tamanho);
        return Decodificar(resposta, tamanho);
    }

    private static void Verificar(int codigo)
    {
        if (codigo >= 0) return;
        throw new DocumentoInvalidoException(UltimoRetorno(TamanhoInicial).Trim());
    }

    private static byte[] Texto(string valor) => Encoding.UTF8.GetBytes(valor + '\0');

    private static string Decodificar(byte[] resposta, int tamanho)
    {
        var fim = Array.IndexOf(resposta, (byte)0);
        var comprimento = fim >= 0 ? fim : Math.Min(tamanho, resposta.Length);
        return Encoding.UTF8.GetString(resposta, 0, comprimento);
    }
}

public sealed class NfseMunicipal(IBibliotecaNfse biblioteca, TabelaMunicipios municipios)
{
    private const int ModoAutomatico = 0;
    private const int ModoLoteAssincrono = 1;
    private const int ModoUnitario = 3;
    private const string MarcaEnvioUnitario = "[FexxoEnvio]\nUnitario=1";
    private const string MarcaLayoutAnterior = "[FexxoLayout]\nAnterior=1";
    private static readonly Dictionary<string, (string Atual, string Anterior)> LayoutsDaReforma = new(StringComparer.Ordinal)
    {
        ["ISSSaoPaulo"] = ("2.00", "1.00"),
    };
    private const int ModoTeste = 4;
    private const string ErroConexao = "X999";

    public ProvedorMunicipalResponse? Provedor(int codigoMunicipio) => municipios.Buscar(codigoMunicipio);

    public EmitirNfseMunicipalResponse Emitir(EmitirNfseMunicipalRequest requisicao)
    {
        if (municipios.Buscar(requisicao.CodigoMunicipio) is null)
        {
            throw new DocumentoInvalidoException($"Município {requisicao.CodigoMunicipio} não tem provedor de NFS-e conhecido.");
        }
        if (requisicao.Ambiente is not ("producao" or "homologacao"))
        {
            throw new AmbienteInvalidoException(requisicao.Ambiente);
        }

        var teste = requisicao.Teste;
        var (resposta, ajustes) = Adaptando(requisicao, ini => Executar(requisicao with { RpsIni = ini }, teste), () => ProximoNumeroDoProvedor(requisicao, teste));
        if (!teste && requisicao.Ambiente == "homologacao" && SemAmbienteDeHomologacao(resposta))
        {
            teste = true;
            (resposta, ajustes) = Adaptando(requisicao, ini => Executar(requisicao with { RpsIni = ini }, teste), () => ProximoNumeroDoProvedor(requisicao, teste));
        }
        return Interpretar(resposta, teste) with { Ajustes = ajustes };
    }

    public ValidarNfseMunicipalResponse Validar(EmitirNfseMunicipalRequest requisicao)
    {
        if (municipios.Buscar(requisicao.CodigoMunicipio) is null)
        {
            throw new DocumentoInvalidoException($"Município {requisicao.CodigoMunicipio} não tem provedor de NFS-e conhecido.");
        }
        var (resposta, ajustes) = Adaptando(requisicao, ini => Executar(requisicao with { RpsIni = ini }, teste: false, somenteGerar: true), () => NumeroParaValidacao);
        if (!PareceJson(resposta)) return new ValidarNfseMunicipalResponse(false, null, [new ErroFiscalDto("acbr", resposta.Trim())], ajustes);
        using var json = JsonDocument.Parse(resposta);
        var lote = json.RootElement.EnumerateObject().First().Value;
        var erros = Erros(lote);
        return new ValidarNfseMunicipalResponse(erros.Count == 0 && Texto(lote, "XmlEnvio") is not null, Texto(lote, "XmlEnvio"), erros, ajustes);
    }

    private const int MaximoDeAjustes = 6;

    private static readonly Dictionary<string, (string Nome, Func<string, string> Aplica)> ElementoRecusado = new(StringComparer.Ordinal)
    {
        ["CodigoServicoNacional"] = ("sem_codigo_nacional", SemChave("CodigoServicoNacional")),
        ["cNBS"] = ("sem_nbs", SemChave("CodigoNBS")),
        ["CodigoNbs"] = ("sem_nbs", SemChave("CodigoNBS")),
        ["NumeroNbs"] = ("sem_nbs", SemChave("CodigoNBS")),
        ["cClassTrib"] = ("sem_classificacao_no_servico", SemChave("cClassTrib")),
        ["IBSCBS"] = ("sem_grupo_ibs_cbs", ini => SemSecao("IBSCBSDPS")(SemSecao("gIBSCBS")(ini))),
        ["IbsCbs"] = ("sem_grupo_ibs_cbs", ini => SemSecao("IBSCBSDPS")(SemSecao("gIBSCBS")(ini))),
        ["CodigoTributacaoMunicipio"] = ("sem_codigo_municipal", SemChave("CodigoTributacaoMunicipio")),
        ["CodigoCnae"] = ("sem_cnae", SemChave("CodigoCnae")),
        ["CodigoTributacaoNacional"] = ("sem_codigo_tributacao_nacional", SemChave("CodigoTributacaoNacional")),
        ["Telefone"] = ("sem_telefone", ini => SemChave("DDD")(SemChave("Telefone")(ini))),
    };

    private static readonly string[] ElementosDeEnderecoDoTomador = ["xBairro", "xLgr", "nro", "Bairro", "Logradouro", "Endereco", "CEP", "Cep", "nrCep", "codigoEstado", "Uf", "UF"];

    private static readonly (string Nome, Func<string, string> Aplica) SemTomador = ("sem_tomador", SemSecao("Tomador"));
    private static readonly (string Nome, Func<string, string> Aplica) EnderecoDoEstabelecimento = ("tomador_com_endereco_do_estabelecimento", TomadorComEnderecoDoEstabelecimento);

    private const string NumeroParaValidacao = "1";

    private static IEnumerable<(string Nome, Func<string, string> Aplica)> AjustesDe(string resposta, Func<string?> numeroDoProvedor)
    {
        if (Regex.IsMatch(resposta, @"(?i)vers[aã]o[^.]{0,60}(schema|layout|leiaute)|(schema|layout|leiaute)[^.]{0,60}vers[aã]o")) yield return ("layout_anterior", ini => ini.Contains(MarcaLayoutAnterior, StringComparison.Ordinal) ? ini : ini.TrimEnd() + "\n\n" + MarcaLayoutAnterior);
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?QuantidadeRps': \[facet 'minInclusive'\]")) yield return ("envio_unitario", ini => ini.Contains(MarcaEnvioUnitario, StringComparison.Ordinal) ? ini : ini.TrimEnd() + "\n\n" + MarcaEnvioUnitario);
        if (resposta.Contains("ConsultarNFSePorFaixa", StringComparison.Ordinal)) yield return ("numero_do_provedor", ini => numeroDoProvedor() is { Length: > 0 } numero ? DefinirNaSecao(ini, "IdentificacaoNFSe", "Numero", numero) : ini);
        if (resposta.Contains("List index (0) out of bounds", StringComparison.Ordinal)) yield return ("com_lista_de_itens", ComListaDeItens);
        if (resposta.Contains("\"\" is an invalid integer", StringComparison.Ordinal)) yield return EnderecoDoEstabelecimento;
        if (resposta.Contains("Codigo nao encontrado na consulta", StringComparison.Ordinal)) yield return ("item_com_desdobro_nacional", ItemComDesdobroNacional);
        if (!resposta.Contains("X800", StringComparison.Ordinal)) yield break;
        var naoEsperado = Regex.Match(resposta, @"Element '(?:\{[^}]*\})?(\w+)': This element is not expected");
        if (naoEsperado.Success && ElementoRecusado.TryGetValue(naoEsperado.Groups[1].Value, out var recusado)) yield return recusado;
        if (naoEsperado.Success && naoEsperado.Groups[1].Value == "Email") yield return ("sem_email", SemChave("Email"));
        if (Regex.IsMatch(resposta, @"Expected is \( (?:\{[^}]*\})?Pais \)") || Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?(?:nmPais|xPais|Pais)': \[facet '(?:minLength|length|pattern)'\] The value (?:''|has a length of '0')"))
        {
            yield return ("com_pais", ComPais);
        }
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?cTribNac': \[facet 'pattern'\]")) yield return ("item_com_codigo_nacional", ItemComCodigoNacional);
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?cTribMun': \[facet 'pattern'\]")) yield return ("sem_codigo_municipal", SemChave("CodigoTributacaoMunicipio"));
        if (Regex.IsMatch(resposta, @"Expected is \( (?:\{[^}]*\})?CodigoServicoNacional \)")) yield return ("com_ctribnac", ComCTribNac);
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?(?:Ddd|DDD)': \[facet 'minLength'\].*minimum length of '3'")) yield return ("ddd_com_zero", DddComZero);
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?Telefone': \[facet 'maxLength'\]")) yield return ElementoRecusado["Telefone"];
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?PisCofinsCst': \[facet 'enumeration'\] The value ''")) yield return ("pis_cofins_sem_incidencia", ini => DefinirNaSecao(ini, "tribFederal", "CST", "00"));
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?TpRetPisCofins': '' is not a valid value")) yield return ("pis_cofins_nao_retido", ini => DefinirNaSecao(ini, "tribFederal", "tpRetPisCofins", "2"));
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?codigoAtividade': \[facet 'pattern'\] The value '\d{7}' is not accepted by the pattern '\[0-9\]\{9\}'")) yield return ("cnae_com_nove_digitos", CnaeComNoveDigitos);
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?codigoEstado': \[facet 'enumeration'\] The value ''") && !resposta.Contains("Tomador", StringComparison.Ordinal)) yield return ("com_uf_da_prestacao", ComUfDaPrestacao);
        var campoVazio = Regex.Match(resposta, @"Element '(?:\{[^}]*\})?(\w+)': \[facet '(?:pattern|minLength|length|enumeration)'\] The value (?:''|has a length of '0')");
        var semEnderecoDoTomador = Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?(Tomador|Endereco|DadosTomador)': Missing child")
            || (campoVazio.Success && ElementosDeEnderecoDoTomador.Contains(campoVazio.Groups[1].Value));
        if (semEnderecoDoTomador)
        {
            yield return EnderecoDoEstabelecimento;
            yield return SemTomador;
        }
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?(ListaServico|itensServico|ListaItens)': Missing child")) yield return ("com_lista_de_itens", ComListaDeItens);
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?tipoRecolhimento': \[facet 'enumeration'\] The value ''")) yield return ("recolhimento_pelo_prestador", ComChave("IdentificacaoRps", "TipoRecolhimento", "1"));
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?ResponsavelRetencao': '' is not a valid value")) yield return ("responsavel_retencao_tomador", ComChave("Servico", "ResponsavelRetencao", "1"));
    }

    private static (string Resposta, List<string> Ajustes) Adaptando(EmitirNfseMunicipalRequest requisicao, Func<string, string> operacao, Func<string?> numeroDoProvedor)
    {
        var ini = requisicao.RpsIni;
        var tentados = new List<string>();
        var aplicados = new List<string>();
        var resposta = operacao(ini);
        for (var tentativa = 0; tentativa < MaximoDeAjustes; tentativa++)
        {
            var proximo = AjustesDe(resposta, numeroDoProvedor).Where(ajuste => !tentados.Contains(ajuste.Nome)).Take(1).ToList();
            if (proximo.Count == 0) break;
            var ajustado = proximo[0].Aplica(ini);
            tentados.Add(proximo[0].Nome);
            if (ajustado == ini) continue;
            aplicados.Add(proximo[0].Nome);
            ini = ajustado;
            resposta = operacao(ini);
        }
        return (resposta, aplicados);
    }

    private static readonly string[] ChavesDoItem = ["ItemListaServico", "CodigoTributacaoMunicipio", "CodigoServicoNacional", "CodigoNBS", "CodigoCnae", "Aliquota", "ValorIss", "BaseCalculo"];

    public static Func<string, string> ComChave(string secao, string chave, string valor) => ini =>
    {
        var linhas = ini.Split('\n').ToList();
        if (linhas.Any(linha => linha.StartsWith(chave + "=", StringComparison.Ordinal))) return ini;
        var cabecalho = linhas.FindIndex(linha => linha.Trim() == $"[{secao}]");
        if (cabecalho < 0) return ini.TrimEnd() + $"\n\n[{secao}]\n{chave}={valor}";
        linhas.Insert(cabecalho + 1, $"{chave}={valor}");
        return string.Join('\n', linhas);
    };

    public static string ComListaDeItens(string ini)
    {
        var valores = ini.Split('\n')
            .Select(linha => linha.Split('=', 2))
            .Where(partes => partes.Length == 2)
            .GroupBy(partes => partes[0].Trim())
            .ToDictionary(grupo => grupo.Key, grupo => grupo.First()[1].Trim());
        string Valor(string chave) => valores.GetValueOrDefault(chave, string.Empty);
        var item = new List<string>
        {
            "[Itens001]",
            $"Descricao={Valor("Discriminacao")}",
            "Quantidade=1",
            $"ValorUnitario={Valor("ValorServicos")}",
            $"ValorTotal={Valor("ValorServicos")}",
            "Tributavel=S",
        };
        item.AddRange(ChavesDoItem.Where(chave => Valor(chave).Length > 0).Select(chave => $"{(chave == "ValorIss" ? "ValorISS" : chave)}={Valor(chave)}"));
        return ini.TrimEnd() + "\n\n" + string.Join('\n', item);
    }

    public static string? ValorNaSecao(string ini, string secao, string chave)
    {
        var dentro = false;
        foreach (var linha in ini.Split('\n'))
        {
            var texto = linha.Trim();
            if (texto.StartsWith('[') && texto.EndsWith(']')) { dentro = texto[1..^1] == secao; continue; }
            if (dentro && texto.StartsWith(chave + "=", StringComparison.Ordinal)) return texto[(chave.Length + 1)..];
        }
        return null;
    }

    public static string DefinirNaSecao(string ini, string secao, string chave, string valor)
    {
        var linhas = ini.Split('\n').ToList();
        var inicio = linhas.FindIndex(linha => linha.Trim() == $"[{secao}]");
        if (inicio < 0) return ini.TrimEnd() + $"\n\n[{secao}]\n{chave}={valor}";
        var fim = linhas.FindIndex(inicio + 1, linha => linha.Trim().StartsWith('['));
        if (fim < 0) fim = linhas.Count;
        var existente = linhas.FindIndex(inicio + 1, fim - inicio - 1, linha => linha.Trim().StartsWith(chave + "=", StringComparison.Ordinal));
        if (existente >= 0) linhas[existente] = $"{chave}={valor}";
        else linhas.Insert(inicio + 1, $"{chave}={valor}");
        return string.Join('\n', linhas);
    }

    public static string ComCTribNac(string ini)
    {
        var nacional = ValorNaSecao(ini, "Servico", "CodigoServicoNacional");
        return string.IsNullOrWhiteSpace(nacional) ? ini : DefinirNaSecao(ini, "Servico", "cTribNac", nacional);
    }

    public static string ComPais(string ini)
    {
        var resultado = DefinirNaSecao(ini, "Prestador", "xPais", "BRASIL");
        return ValorNaSecao(ini, "Tomador", "CNPJCPF") is null ? resultado : DefinirNaSecao(resultado, "Tomador", "xPais", "BRASIL");
    }

    public static string ItemComDesdobroNacional(string ini)
    {
        var nacional = ValorNaSecao(ini, "Servico", "CodigoServicoNacional");
        if (nacional is not { Length: 6 } || !nacional.All(char.IsDigit)) return ini;
        return DefinirNaSecao(ini, "Servico", "ItemListaServico", $"{nacional[..2]}.{nacional[2..4]}.{nacional[4..]}.000");
    }

    public static string CnaeComNoveDigitos(string ini)
    {
        var cnae = ValorNaSecao(ini, "Servico", "CodigoCnae");
        return cnae is { Length: 7 } ? DefinirNaSecao(ini, "Servico", "CodigoCnae", cnae + "00") : ini;
    }

    public static string ComUfDaPrestacao(string ini)
    {
        var uf = ValorNaSecao(ini, "Prestador", "UF");
        return string.IsNullOrWhiteSpace(uf) ? ini : DefinirNaSecao(ini, "Servico", "UFPrestacao", uf);
    }

    public static string DddComZero(string ini) =>
        string.Join('\n', ini.Split('\n').Select(linha => Regex.IsMatch(linha.Trim(), @"^DDD=\d{2}$") ? $"DDD=0{linha.Trim()[4..]}" : linha));

    private static readonly string[] ChavesDeEndereco = ["Logradouro", "Numero", "Bairro", "CEP", "CodigoMunicipio", "UF", "xMunicipio"];

    public static string TomadorComEnderecoDoEstabelecimento(string ini)
    {
        if (ValorNaSecao(ini, "Tomador", "CNPJCPF") is null) return ini;
        if (!string.IsNullOrWhiteSpace(ValorNaSecao(ini, "Tomador", "CEP"))) return ini;
        var resultado = ini;
        foreach (var chave in ChavesDeEndereco)
        {
            var doPrestador = ValorNaSecao(ini, "Prestador", chave);
            if (!string.IsNullOrWhiteSpace(doPrestador)) resultado = DefinirNaSecao(resultado, "Tomador", chave, doPrestador);
        }
        return resultado;
    }

    public static Func<string, string> SemChave(string chave) => ini =>
        string.Join('\n', ini.Split('\n').Where(linha => !linha.StartsWith(chave + "=", StringComparison.Ordinal)));

    public static string ItemComCodigoNacional(string ini)
    {
        var linhas = ini.Split('\n');
        var nacional = linhas.FirstOrDefault(linha => linha.StartsWith("CodigoServicoNacional=", StringComparison.Ordinal))?.Split('=', 2)[1];
        if (string.IsNullOrWhiteSpace(nacional)) return ini;
        return string.Join('\n', linhas.Select(linha => linha.StartsWith("ItemListaServico=", StringComparison.Ordinal) ? $"ItemListaServico={nacional}" : linha));
    }

    public static Func<string, string> SemSecao(string nome) => ini =>
    {
        var linhas = new List<string>();
        var dentro = false;
        foreach (var linha in ini.Split('\n'))
        {
            var cabecalho = linha.Trim();
            if (cabecalho.StartsWith('[') && cabecalho.EndsWith(']')) dentro = cabecalho[1..^1] == nome;
            if (!dentro) linhas.Add(linha);
        }
        return string.Join('\n', linhas);
    };

    private string Executar(EmitirNfseMunicipalRequest requisicao, bool teste, bool somenteGerar = false) =>
        NaSessao(requisicao, teste, () =>
        {
            var unitario = requisicao.RpsIni.Contains(MarcaEnvioUnitario, StringComparison.Ordinal);
            var provedor = municipios.Buscar(requisicao.CodigoMunicipio)?.Provedor;
            if (provedor is not null && LayoutsDaReforma.TryGetValue(provedor, out var layouts))
            {
                biblioteca.DefinirVersaoDoLayout(requisicao.RpsIni.Contains(MarcaLayoutAnterior, StringComparison.Ordinal) ? layouts.Anterior : layouts.Atual);
            }
            biblioteca.CarregarRps(requisicao.RpsIni
                .Replace(MarcaEnvioUnitario, string.Empty, StringComparison.Ordinal)
                .Replace(MarcaLayoutAnterior, string.Empty, StringComparison.Ordinal)
                .TrimEnd());
            if (somenteGerar) return biblioteca.GerarLote(requisicao.Lote, unitario ? ModoUnitario : ModoAutomatico);
            return biblioteca.Emitir(requisicao.Lote, teste ? ModoTeste : unitario ? ModoUnitario : ModoLoteAssincrono);
        });

    private string? ProximoNumeroDoProvedor(EmitirNfseMunicipalRequest requisicao, bool teste)
    {
        var resposta = NaSessao(requisicao, teste, () => biblioteca.ConsultarNfsePorFaixa("1", "1", 1));
        if (!PareceJson(resposta)) return null;
        using var json = JsonDocument.Parse(resposta);
        return Procurar(json.RootElement, "NumeroNota") is { Length: > 0 } numero ? numero : null;
    }

    private static string? Procurar(JsonElement elemento, string propriedade)
    {
        if (elemento.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in elemento.EnumerateObject())
            {
                if (item.Name == propriedade && item.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return item.Value.ToString();
                if (Procurar(item.Value, propriedade) is { } encontrado) return encontrado;
            }
        }
        if (elemento.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in elemento.EnumerateArray())
            {
                if (Procurar(item, propriedade) is { } encontrado) return encontrado;
            }
        }
        return null;
    }

    private string NaSessao(EmitirNfseMunicipalRequest requisicao, bool teste, Func<string> acao)
    {
        lock (BibliotecaAcbr.Sincronizacao)
        {
            biblioteca.GravarConfiguracao("NFSe", "CodigoMunicipio", requisicao.CodigoMunicipio.ToString());
            biblioteca.GravarConfiguracao("NFSe", "Ambiente", teste || requisicao.Ambiente == "producao" ? "0" : "1");
            biblioteca.GravarConfiguracao("NFSe", "Emitente.CNPJ", requisicao.Emitente.Cnpj);
            biblioteca.GravarConfiguracao("NFSe", "Emitente.InscMun", requisicao.Emitente.InscricaoMunicipal ?? string.Empty);
            biblioteca.GravarConfiguracao("NFSe", "Emitente.RazSocial", requisicao.Emitente.RazaoSocial);
            biblioteca.GravarConfiguracao("NFSe", "Emitente.WSUser", requisicao.Emitente.UsuarioWebservice ?? string.Empty);
            biblioteca.GravarConfiguracao("NFSe", "Emitente.WSSenha", requisicao.Emitente.SenhaWebservice ?? string.Empty);
            biblioteca.GravarConfiguracao("NFSe", "Emitente.WSChaveAcesso", requisicao.Emitente.ChaveAcessoWebservice ?? string.Empty);
            biblioteca.GravarConfiguracao("NFSe", "Emitente.WSChaveAutoriz", requisicao.Emitente.ChaveAutorizacaoWebservice ?? string.Empty);
            biblioteca.GravarConfiguracao("NFSe", "CNPJPrefeitura", requisicao.Emitente.CnpjPrefeitura ?? string.Empty);
            biblioteca.GravarConfiguracao("DFe", "DadosPFX", requisicao.Certificado.PfxBase64);
            biblioteca.GravarConfiguracao("DFe", "Senha", requisicao.Certificado.Senha);
            biblioteca.UsarBibliotecasSsl();
            try
            {
                return acao();
            }
            finally
            {
                biblioteca.GravarConfiguracao("DFe", "DadosPFX", string.Empty);
                biblioteca.GravarConfiguracao("DFe", "Senha", string.Empty);
                biblioteca.GravarConfiguracao("NFSe", "Emitente.WSSenha", string.Empty);
                biblioteca.GravarConfiguracao("NFSe", "Emitente.WSChaveAcesso", string.Empty);
                biblioteca.GravarConfiguracao("NFSe", "Emitente.WSChaveAutoriz", string.Empty);
            }
        }
    }

    private static bool SemAmbienteDeHomologacao(string resposta) =>
        resposta.Contains("URL de Homologa", StringComparison.OrdinalIgnoreCase);

    private static bool PareceJson(string resposta) => resposta.TrimStart().StartsWith('{');

    public static EmitirNfseMunicipalResponse Interpretar(string resposta, bool teste)
    {
        if (!PareceJson(resposta))
        {
            return new EmitirNfseMunicipalResponse(StatusNfseMunicipal.Rejeitada, null, null, null, null, null, null, [new ErroFiscalDto("acbr", resposta.Trim())]);
        }
        using var json = JsonDocument.Parse(resposta);
        var envio = json.RootElement.EnumerateObject().First().Value;
        var erros = Erros(envio);
        if (erros.Any(erro => erro.Codigo == ErroConexao) && CertificadoRecusado(Texto(envio, "XmlRetorno")))
        {
            throw new CertificadoInvalidoException("A prefeitura recusou o certificado digital. Confira se ele é o e-CNPJ da empresa e está dentro da validade.");
        }
        if (erros.Any(erro => erro.Codigo == ErroConexao))
        {
            throw new ServicoIndisponivelException($"A prefeitura não respondeu: {erros.First(erro => erro.Codigo == ErroConexao).Mensagem}");
        }

        var numero = Texto(envio, "NumeroNota");
        var sucesso = envio.TryGetProperty("Sucesso", out var campo) && campo.ValueKind == JsonValueKind.True;
        var status = erros.Count > 0
            ? StatusNfseMunicipal.Rejeitada
            : teste ? StatusNfseMunicipal.Validada
            : numero is not null ? StatusNfseMunicipal.Autorizada
            : sucesso ? StatusNfseMunicipal.EmProcessamento
            : StatusNfseMunicipal.Rejeitada;

        return new EmitirNfseMunicipalResponse(
            status,
            numero,
            Texto(envio, "CodigoVerificacao"),
            Texto(envio, "Protocolo"),
            Texto(envio, "Link"),
            Texto(envio, "XmlEnvio"),
            Texto(envio, "XmlRetorno"),
            erros);
    }

    private static bool CertificadoRecusado(string? retorno) =>
        retorno is not null && (retorno.Contains("permission to view", StringComparison.OrdinalIgnoreCase) || retorno.Contains("403 - Forbidden", StringComparison.OrdinalIgnoreCase));

    private static List<ErroFiscalDto> Erros(JsonElement envio) =>
        envio.EnumerateObject()
            .Where(propriedade => propriedade.Name.StartsWith("Erro", StringComparison.Ordinal) && propriedade.Value.ValueKind == JsonValueKind.Object)
            .Select(propriedade => new ErroFiscalDto(
                Texto(propriedade.Value, "Codigo") ?? propriedade.Name,
                string.Join(" ", new[] { Texto(propriedade.Value, "Descricao"), Texto(propriedade.Value, "Correcao") }.Where(parte => parte is not null))))
            .ToList();

    private static string? Texto(JsonElement elemento, string nome) =>
        elemento.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(valor.GetString())
            ? valor.GetString()
            : null;
}

public sealed class TabelaMunicipios
{
    private const string ProvedorNacional = "PadraoNacional";
    private readonly Dictionary<int, ProvedorMunicipalResponse> municipios;

    public TabelaMunicipios(string arquivoServicos)
    {
        municipios = File.Exists(arquivoServicos) ? Ler(File.ReadAllText(arquivoServicos, Encoding.Latin1)) : new Dictionary<int, ProvedorMunicipalResponse>();
    }

    public TabelaMunicipios(Dictionary<int, ProvedorMunicipalResponse> municipios) => this.municipios = municipios;

    public int Total => municipios.Count;

    public ProvedorMunicipalResponse? Buscar(int codigoMunicipio) => municipios.GetValueOrDefault(codigoMunicipio);

    public static Dictionary<int, ProvedorMunicipalResponse> Ler(string conteudo)
    {
        var resultado = new Dictionary<int, ProvedorMunicipalResponse>();
        int? codigo = null;
        string nome = string.Empty, uf = string.Empty, provedor = string.Empty;

        void Fechar()
        {
            if (codigo is int atual && provedor.Length > 0)
            {
                resultado[atual] = new ProvedorMunicipalResponse(atual, nome, uf, provedor, provedor == ProvedorNacional);
            }
        }

        foreach (var linhaBruta in conteudo.Split('\n'))
        {
            var linha = linhaBruta.Trim();
            if (linha.StartsWith('[') && linha.EndsWith(']'))
            {
                Fechar();
                codigo = int.TryParse(linha[1..^1], out var lido) && lido > 999999 ? lido : null;
                nome = uf = provedor = string.Empty;
                continue;
            }
            if (codigo is null || linha.StartsWith(';')) continue;
            var separador = linha.IndexOf('=');
            if (separador <= 0) continue;
            var valor = linha[(separador + 1)..].Trim();
            switch (linha[..separador].Trim())
            {
                case "Nome": nome = valor; break;
                case "UF": uf = valor; break;
                case "Provedor": provedor = valor; break;
            }
        }
        Fechar();
        return resultado;
    }
}
