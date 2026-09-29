#!/usr/bin/env bash
set -euo pipefail
cd "${ACBR_NFSEX_DIR:-/acbr/Fontes/ACBrDFe/ACBrNFSeX}"

aplicar() {
  local arquivo="$1" expressao="$2"
  PADRAO="$expressao" perl -0pi -e '$n = eval $ENV{PADRAO}; die "patch sem efeito em $ARGV\n" unless $n;' "$arquivo"
}

aplicar Provedores/TaxTecnologia.Provider.pas 's/Result := Result \+ \x27\\(Producao|Homologacao)\x27/Result := PathWithDelim(Result) + \x27$1\x27/g'
aplicar Provedores/GeisWeb.GravarXml.pas 's/[ \t]*Result\.AppendChild\(AddNode\(tcStr, \x27#1\x27, \x27ProvReg\x27,[^;]*;\r?\n//s'
aplicar Base/Provedores/ACBrNFSeX.LerIni.pas 's/([ \t]*)(NFSe\.Servico\.CodigoNBS := AINIRec\.ReadString\(sSecao, \x27CodigoNBS\x27, \x27\x27\);)(\r?\n)/$1$2$3$1NFSe.Servico.CodigoTributacaoNacional := AINIRec.ReadString(sSecao, \x27CodigoTributacaoNacional\x27, \x27\x27);$3/'
aplicar Base/Provedores/ACBrNFSeX.LerIni.pas 's/([ \t]*NFSe\.CodigoVerificacao := NFSe\.infNFSe\.ID;\r?\n)([ \t]*)end;(\r?\n)/$1${2}end${3}${2}else${3}${2}  NFSe.Numero := AINIRec.ReadString(sSecao, \x27Numero\x27, \x27\x27);$3/'
aplicar ../../ACBrTCP/ACBrSocket.pas 's/\{\$IFDEF NOGUI\}(\r?\n)[ \t]*(\{\$UNDEF UPDATE_SCREEN_CURSOR\})\r?\n\{\$ENDIF\}/${2}/'
aplicar Base/Provedores/ACBrNFSeXLerXml_ABRASFv1.pas 's/([ \t]*)(NFSe\.Servico\.Valores\.DescontoCondicionado := StringToFloatDef\(AINIRec\.ReadString\(LSecao, \x27DescontoCondicionado\x27, \x27\x27\), 0\);)(\r?\n)/$1$2$3$1NFSe.Servico.Valores.tribFed.CST := StrToCST(Ok, AINIRec.ReadString(\x27tribFederal\x27, \x27CST\x27, \x27\x27));$3$1NFSe.Servico.Valores.tribFed.tpRetPisCofins := StrTotpRetPisCofins(Ok, AINIRec.ReadString(\x27tribFederal\x27, \x27tpRetPisCofins\x27, \x27\x27));$3/'
aplicar Provedores/Aspec.LerJson.pas 's/([ \t]*)(NFSe\.Servico\.CodigoCnae := AINIRec\.ReadString\(sSecao, \x27CodigoCnae\x27, \x27\x27\);)(\r?\n)/$1$2$3$1NFSe.Servico.CodigoNBS := AINIRec.ReadString(sSecao, \x27CodigoNBS\x27, \x27\x27);$3$1NFSe.IBSCBS.cIndOp := AINIRec.ReadString(\x27IBSCBSDPS\x27, \x27cIndOp\x27, \x27\x27);$3$1NFSe.IBSCBS.valores.trib.gIBSCBS.cClassTrib := AINIRec.ReadString(\x27gIBSCBS\x27, \x27cClassTrib\x27, \x27\x27);$3/'
echo "patches do ACBr aplicados"
