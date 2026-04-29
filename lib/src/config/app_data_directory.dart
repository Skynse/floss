import 'dart:io';

import 'package:path_provider/path_provider.dart';

class AppDataDirectory {
  static String? _path;

  static Future<String> getPath() async {
    if (_path != null) return _path!;
    
    final docsDir = await getApplicationDocumentsDirectory();
    final flossDir = Directory('${docsDir.path}/floss');
    
    if (!await flossDir.exists()) {
      await flossDir.create(recursive: true);
    }
    
    // Create subdirectories
    await Directory('${flossDir.path}/brushes').create(recursive: true);
    await Directory('${flossDir.path}/documents').create(recursive: true);
    
    _path = flossDir.path;
    return _path!;
  }
  
  static Future<String> getConfigPath() async {
    final dir = await getPath();
    return '$dir/config.json';
  }
  
  static Future<String> getBrushesPath() async {
    final dir = await getPath();
    return '$dir/brushes';
  }
  
  static Future<String> getDocumentsPath() async {
    final dir = await getPath();
    return '$dir/documents';
  }
}
