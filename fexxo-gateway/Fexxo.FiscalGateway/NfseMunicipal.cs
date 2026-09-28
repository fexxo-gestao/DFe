using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fexxo.FiscalGateway;

public sealed record EmitenteMunicipalDto(string Cnpj, string? InscricaoMunicipal, string RazaoSocial);

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
    string GerarLote(string lote);
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

    public string GerarLote(string lote)
    {
        var tamanho = TamanhoInicial;
        var resposta = new byte[tamanho];
        var codigo = AcbrNfseNativo.GerarLote(Texto(lote), 1, 0, resposta, ref tamanho);
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
    private const int ModoLoteAssincrono = 1;
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
        var (resposta, ajustes) = Adaptando(requisicao, ini => Executar(requisicao with { RpsIni = ini }, teste));
        if (!teste && requisicao.Ambiente == "homologacao" && SemAmbienteDeHomologacao(resposta))
        {
            teste = true;
            (resposta, ajustes) = Adaptando(requisicao, ini => Executar(requisicao with { RpsIni = ini }, teste));
        }
        return Interpretar(resposta, teste) with { Ajustes = ajustes };
    }

    public ValidarNfseMunicipalResponse Validar(EmitirNfseMunicipalRequest requisicao)
    {
        if (municipios.Buscar(requisicao.CodigoMunicipio) is null)
        {
            throw new DocumentoInvalidoException($"Município {requisicao.CodigoMunicipio} não tem provedor de NFS-e conhecido.");
        }
        var (resposta, ajustes) = Adaptando(requisicao, ini => Executar(requisicao with { RpsIni = ini }, teste: false, somenteGerar: true));
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
    };

    private static (string Nome, Func<string, string> Aplica)? AjusteDe(string resposta)
    {
        if (!resposta.Contains("X800", StringComparison.Ordinal)) return null;
        var naoEsperado = Regex.Match(resposta, @"Element '(?:\{[^}]*\})?(\w+)': This element is not expected");
        if (naoEsperado.Success && ElementoRecusado.TryGetValue(naoEsperado.Groups[1].Value, out var ajuste)) return ajuste;
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?cTribNac': \[facet 'pattern'\]")) return ("item_com_codigo_nacional", ItemComCodigoNacional);
        if (Regex.IsMatch(resposta, @"Element '(?:\{[^}]*\})?Tomador': Missing child")) return ("sem_tomador", SemSecao("Tomador"));
        return null;
    }

    private static (string Resposta, List<string> Ajustes) Adaptando(EmitirNfseMunicipalRequest requisicao, Func<string, string> operacao)
    {
        var ini = requisicao.RpsIni;
        var aplicados = new List<string>();
        var resposta = operacao(ini);
        for (var tentativa = 0; tentativa < MaximoDeAjustes; tentativa++)
        {
            var ajuste = AjusteDe(resposta);
            if (ajuste is null || aplicados.Contains(ajuste.Value.Nome)) break;
            ini = ajuste.Value.Aplica(ini);
            aplicados.Add(ajuste.Value.Nome);
            resposta = operacao(ini);
        }
        return (resposta, aplicados);
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

    private string Executar(EmitirNfseMunicipalRequest requisicao, bool teste, bool somenteGerar = false)
    {
        lock (BibliotecaAcbr.Sincronizacao)
        {
            biblioteca.GravarConfiguracao("NFSe", "CodigoMunicipio", requisicao.CodigoMunicipio.ToString());
            biblioteca.GravarConfiguracao("NFSe", "Ambiente", teste || requisicao.Ambiente == "producao" ? "0" : "1");
            biblioteca.GravarConfiguracao("NFSe", "Emitente.CNPJ", requisicao.Emitente.Cnpj);
            biblioteca.GravarConfiguracao("NFSe", "Emitente.InscMun", requisicao.Emitente.InscricaoMunicipal ?? string.Empty);
            biblioteca.GravarConfiguracao("NFSe", "Emitente.RazSocial", requisicao.Emitente.RazaoSocial);
            biblioteca.GravarConfiguracao("DFe", "DadosPFX", requisicao.Certificado.PfxBase64);
            biblioteca.GravarConfiguracao("DFe", "Senha", requisicao.Certificado.Senha);
            biblioteca.UsarBibliotecasSsl();
            try
            {
                biblioteca.CarregarRps(requisicao.RpsIni);
                return somenteGerar ? biblioteca.GerarLote(requisicao.Lote) : biblioteca.Emitir(requisicao.Lote, teste ? ModoTeste : ModoLoteAssincrono);
            }
            finally
            {
                biblioteca.GravarConfiguracao("DFe", "DadosPFX", string.Empty);
                biblioteca.GravarConfiguracao("DFe", "Senha", string.Empty);
            }
        }
    }

    private static bool SemAmbienteDeHomologacao(string resposta) =>
        resposta.Contains("URL de Homologa", StringComparison.OrdinalIgnoreCase);

    public static EmitirNfseMunicipalResponse Interpretar(string resposta, bool teste)
    {
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
