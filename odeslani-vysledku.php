<?php
header('Content-Type: application/json');

$raw  = file_get_contents('php://input');
$data = json_decode($raw, true);

if (!$data || !isset($data['url']) || !isset($data['result'])) {
  http_response_code(400);
  echo json_encode(['status'=>'error','message'=>'Invalid data']);
  exit;
}

$url    = trim((string)$data['url']);
$allOk  = !empty($data['allOk']);
$checks = is_array($data['result']) ? $data['result'] : [];

$to      = 'theadvertninja@gmail.com'; 
$from    = 'cemex@advert.ninja';           
$subject = "Výsledek auditu pro ".$url;

$html  = "<h2>Výsledek auditu</h2>";
$html .= "<p><strong>URL:</strong> ".htmlspecialchars($url, ENT_QUOTES, 'UTF-8')."</p>";
$html .= "<p><strong>Celkový výsledek:</strong> ".($allOk ? "PASS ✅" : "FAIL ❌")."</p><ul>";
foreach ($checks as $c) {
  $ok = !empty($c['Ok']);
  $lab = htmlspecialchars($c['Check'] ?? '', ENT_QUOTES, 'UTF-8');
  $det = htmlspecialchars($c['Details'] ?? '', ENT_QUOTES, 'UTF-8');
  $html .= "<li>".($ok ? "✅" : "❌")." {$lab}".($det ? " – {$det}" : "")."</li>";
}
$html .= "</ul><p style='color:#777'>Odesláno: ".date('Y-m-d H:i:s')."</p>";

$headers  = "MIME-Version: 1.0\r\n";
$headers .= "Content-Type: text/html; charset=UTF-8\r\n";
$headers .= "From: MigrationBob <{$from}>\r\n";
$headers .= "Reply-To: {$from}\r\n";




$ok = @mail($to, $subject, $html, $headers, "-f{$from}");
echo json_encode($ok ? ['status'=>'success'] : ['status'=>'error','message'=>'mail() failed']);
