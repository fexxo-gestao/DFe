using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;
using Unimake.Business.DFe.Servicos;
using Xunit;
using NFCeServicos = Unimake.Business.DFe.Servicos.NFCe;
using NFSeServicos = Unimake.Business.DFe.Servicos.NFSe;

namespace Unimake.DFe.Test.Multiplataforma
{
    public class AssinaturaMultiplataformaTest
    {
        private const string NamespaceNFSe = "http://www.sped.fazenda.gov.br/nfse";
        private const string NamespaceNFe = "http://www.portalfiscal.inf.br/nfe";
        private const int CodigoPadraoNacional = 1001058;

        [Fact]
        [Trait("DFe", "NFSe")]
        [Trait("Plataforma", "Multiplataforma")]
        public void GerarNfseNacional_AssinaEValidaDpsComIbsCbsSemCertificadoEmArquivo()
        {
            using var certificado = CriarCertificadoAutoassinado();
            var dps = CarregarSemAssinatura("NFSe", "Resources", "NACIONAL", "1.01", "GerarNfseEnvio-env-loterps.xml");

            var servico = new NFSeServicos.GerarNfse(dps, new Configuracao
            {
                TipoDFe = TipoDFe.NFSe,
                PadraoNFSe = PadraoNFSe.NACIONAL,
                CodigoMunicipio = CodigoPadraoNacional,
                TipoAmbiente = TipoAmbiente.Homologacao,
                Servico = Servico.NFSeGerarNfse,
                SchemaVersao = "1.01",
                CertificadoDigital = certificado
            });

            var assinado = servico.ConteudoXMLAssinado;
            Assert.True(AssinaturaValida(assinado, certificado));
            Assert.Equal(1, assinado.GetElementsByTagName("IBSCBS", NamespaceNFSe).Count);
        }

        [Fact]
        [Trait("DFe", "NFCe")]
        [Trait("Plataforma", "Multiplataforma")]
        public void AutorizacaoNFCe_AssinaValidaSchemaDaReformaEGeraQrCodeSemCertificadoEmArquivo()
        {
            using var certificado = CriarCertificadoAutoassinado();
            var nfce = CarregarSemAssinatura("NFe", "Resources", "RTC", "NFCe_CST800.xml");
            foreach (XmlNode classificacao in nfce.GetElementsByTagName("cClassTrib", NamespaceNFe))
            {
                classificacao.InnerText = "000001";
            }
            foreach (var suplemento in nfce.GetElementsByTagName("infNFeSupl", NamespaceNFe).Cast<XmlNode>().ToList())
            {
                suplemento.ParentNode.RemoveChild(suplemento);
            }

            var servico = new NFCeServicos.Autorizacao(nfce.OuterXml, new Configuracao
            {
                TipoDFe = TipoDFe.NFCe,
                TipoAmbiente = TipoAmbiente.Homologacao,
                CSC = "0123456789ABCDEF0123456789ABCDEF",
                CSCIDToken = 1,
                CertificadoDigital = certificado
            });

            var assinado = servico.ConteudoXMLAssinado;
            Assert.True(AssinaturaValida(assinado, certificado));
            Assert.Equal(1, assinado.GetElementsByTagName("IBSCBS", NamespaceNFe).Count);
            var qrCode = assinado.GetElementsByTagName("qrCode", NamespaceNFe);
            Assert.Equal(1, qrCode.Count);
            Assert.StartsWith("http", qrCode[0].InnerText.Trim());
        }

        private static X509Certificate2 CriarCertificadoAutoassinado()
        {
            using var rsa = RSA.Create(2048);
            var requisicao = new CertificateRequest("CN=UNIMAKE MULTIPLATAFORMA:12345678000195", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var efemero = requisicao.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
            return new X509Certificate2(efemero.Export(X509ContentType.Pfx, "multiplataforma"), "multiplataforma", X509KeyStorageFlags.Exportable);
        }

        private static XmlDocument CarregarSemAssinatura(params string[] caminhoRelativo)
        {
            var caminho = Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", ".." }.Concat(caminhoRelativo).ToArray());
            var documento = new XmlDocument();
            documento.Load(caminho);
            foreach (var assinatura in documento.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl).Cast<XmlNode>().ToList())
            {
                assinatura.ParentNode.RemoveChild(assinatura);
            }
            return documento;
        }

        private static bool AssinaturaValida(XmlDocument documento, X509Certificate2 certificado)
        {
            var assinaturas = documento.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl);
            Assert.Equal(1, assinaturas.Count);
            var signedXml = new SignedXml(documento);
            signedXml.LoadXml((XmlElement)assinaturas[0]);
            return signedXml.CheckSignature(certificado, true);
        }
    }
}
